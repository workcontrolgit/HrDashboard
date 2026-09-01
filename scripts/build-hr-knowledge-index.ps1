<#
.SYNOPSIS
    Builds the HR position-descriptions Azure AI Search index and registers it
    as Azure AI Foundry Knowledge on proj-dotnet-ai-agent.
.DESCRIPTION
    Merges TEMP_PD_SCHED_PC (position headers, exported from the ACRS Oracle
    connection) with TEMP_PD_SCHED_PC_DUTIES (duty rows) into one combined
    document per position, embeds each with the text-embedding-3-small
    deployment on hr-mcp-ai-foundry, and pushes them into a purpose-built
    vector index. Both the Search calls and the embedding calls authenticate
    with the caller's own Azure AD identity (via `az account get-access-token`)
    rather than API keys, since the Search service has local (key) auth
    disabled. Finishes by registering the populated index as a Foundry
    Knowledge asset via `az ml index create`.
#>

param(
    [string]$ResourceGroup = "rg-dotnet-ai-agent",
    [string]$ProjectName = "proj-dotnet-ai-agent",
    [string]$SearchServiceName = "hr-mcp-ai-foundry-project-srch-p006",
    [string]$AoaiEndpoint = "https://hr-mcp-ai-foundry.cognitiveservices.azure.com",
    [string]$EmbeddingDeployment = "text-embedding-3-small",
    [int]$EmbeddingDimensions = 1536,
    [string]$SearchIndexName = "hr-position-descriptions",
    [string]$PdJsonPath = (Join-Path $PSScriptRoot "..\.local-data\hr-knowledge\TEMP_PD_SCHED_PC.json"),
    [string]$DutiesJsonPath = (Join-Path $PSScriptRoot "..\.local-data\hr-knowledge\TEMP_PD_SCHED_PC_DUTIES.json"),
    [string]$SearchConnectionName = "hr-knowledge-search",
    [string]$AoaiConnectionName = "hr-knowledge-aoai",
    [int]$StartAtDocument = 0
)

$ErrorActionPreference = "Stop"

$searchEndpoint = "https://$SearchServiceName.search.windows.net"
$searchApiVersion = "2024-07-01"
$aoaiApiVersion = "2023-05-15"

function Get-AadToken {
    param([string]$Resource)
    (az account get-access-token --resource $Resource --query accessToken -o tsv)
}

function Invoke-RestMethodWithRetry {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [string]$Body,
        [int]$MaxRetries = 8
    )
    $attempt = 0
    while ($true) {
        try {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -Body $Body
        }
        catch {
            $response = $_.Exception.Response
            $isRateLimited = $response -and [int]$response.StatusCode -eq 429
            if (-not $isRateLimited -or $attempt -ge $MaxRetries) { throw }

            $retryAfter = $null
            if ($response.Headers -and $response.Headers["Retry-After"]) {
                $retryAfter = [int]$response.Headers["Retry-After"]
            }
            $waitSeconds = if ($retryAfter) { $retryAfter } else { [Math]::Min(60, [Math]::Pow(2, $attempt) * 3) }

            $attempt++
            Write-Host "    Rate limited, waiting ${waitSeconds}s (attempt $attempt/$MaxRetries)..." -ForegroundColor Yellow
            Start-Sleep -Seconds $waitSeconds
        }
    }
}

Write-Host "Registering Azure ML connections on $ProjectName..." -ForegroundColor Cyan

$searchConnectionFile = New-TemporaryFile
@"
name: $SearchConnectionName
type: azure_ai_search
endpoint: $searchEndpoint
credentials:
  type: aad
"@ | Set-Content -Path $searchConnectionFile
az ml connection create --file $searchConnectionFile --resource-group $ResourceGroup --workspace-name $ProjectName | Out-Null

$aoaiConnectionFile = New-TemporaryFile
@"
name: $AoaiConnectionName
type: azure_open_ai
azure_endpoint: $AoaiEndpoint
credentials:
  type: aad
"@ | Set-Content -Path $aoaiConnectionFile
az ml connection create --file $aoaiConnectionFile --resource-group $ResourceGroup --workspace-name $ProjectName | Out-Null

Write-Host "Creating Azure AI Search index '$SearchIndexName'..." -ForegroundColor Cyan

$searchToken = Get-AadToken -Resource "https://search.azure.com"
$searchHeaders = @{ Authorization = "Bearer $searchToken"; "Content-Type" = "application/json" }

$indexSchema = @{
    name   = $SearchIndexName
    fields = @(
        @{ name = "id"; type = "Edm.String"; key = $true; filterable = $true }
        @{ name = "content"; type = "Edm.String"; searchable = $true }
        @{ name = "contentVector"; type = "Collection(Edm.Single)"; searchable = $true; dimensions = $EmbeddingDimensions; vectorSearchProfile = "hr-vector-profile" }
        @{ name = "pdNbr"; type = "Edm.String"; filterable = $true }
        @{ name = "positionTitle"; type = "Edm.String"; searchable = $true; filterable = $true }
        @{ name = "orgDesc"; type = "Edm.String"; filterable = $true; facetable = $true }
        @{ name = "bureauDesc"; type = "Edm.String"; filterable = $true; facetable = $true }
        @{ name = "grdCode"; type = "Edm.String"; filterable = $true; facetable = $true }
        @{ name = "gvtOccSeries"; type = "Edm.String"; filterable = $true; facetable = $true }
        @{ name = "schedulePcInd"; type = "Edm.String"; filterable = $true; facetable = $true }
    )
    vectorSearch = @{
        algorithms = @(@{ name = "hr-hnsw"; kind = "hnsw" })
        profiles   = @(@{ name = "hr-vector-profile"; algorithm = "hr-hnsw" })
    }
    semantic = @{
        configurations = @(
            @{
                name              = "default"
                prioritizedFields = @{
                    titleField                = @{ fieldName = "positionTitle" }
                    prioritizedContentFields   = @(@{ fieldName = "content" })
                    prioritizedKeywordsFields  = @(
                        @{ fieldName = "orgDesc" }
                        @{ fieldName = "bureauDesc" }
                        @{ fieldName = "gvtOccSeries" }
                    )
                }
            }
        )
    }
} | ConvertTo-Json -Depth 10

Invoke-RestMethod -Method Put -Uri "$searchEndpoint/indexes/${SearchIndexName}?api-version=$searchApiVersion" -Headers $searchHeaders -Body $indexSchema | Out-Null

Write-Host "Loading and merging source JSON..." -ForegroundColor Cyan

$pdRows = (Get-Content $PdJsonPath -Raw | ConvertFrom-Json).results[0].items
$dutyRows = (Get-Content $DutiesJsonPath -Raw | ConvertFrom-Json).results[0].items

$dutiesByPd = @{}
foreach ($duty in $dutyRows) {
    $key = [string]$duty.pd_seq_num
    if (-not $dutiesByPd.ContainsKey($key)) { $dutiesByPd[$key] = @() }
    $dutiesByPd[$key] += $duty
}

$documents = foreach ($pd in $pdRows) {
    $key = [string]$pd.pd_seq_num
    $duties = $dutiesByPd[$key]
    $dutiesText = if ($duties) {
        ($duties | Sort-Object pdd_percent_time_spent -Descending |
            ForEach-Object { "($($_.pdd_percent_time_spent)%) $($_.pdd_major_duties_text)" }) -join "`n"
    } else { "" }

    $content = @"
Position: $($pd.pd_position_title_text) (PD $($pd.pd_nbr))
Organization: $($pd.org_desc) / $($pd.bureau_desc)
Pay plan/grade: $($pd.gvt_pay_plan) $($pd.grd_code), Occupational series: $($pd.gvt_occ_series)
Sensitivity: $($pd.position_sensitivity_desc), Security clearance: $($pd.security_clearance_desc)
$($pd.pd_intro)

Major duties:
$dutiesText
"@

    [pscustomobject]@{
        id            = $key
        content       = $content
        pdNbr         = [string]$pd.pd_nbr
        positionTitle = [string]$pd.pd_position_title_text
        orgDesc       = [string]$pd.org_desc
        bureauDesc    = [string]$pd.bureau_desc
        grdCode       = [string]$pd.grd_code
        gvtOccSeries  = [string]$pd.gvt_occ_series
        schedulePcInd = [string]$pd.schedule_pc_ind
    }
}

Write-Host "Embedding and uploading $($documents.Count) documents..." -ForegroundColor Cyan

$aoaiToken = Get-AadToken -Resource "https://cognitiveservices.azure.com"
$aoaiHeaders = @{ Authorization = "Bearer $aoaiToken"; "Content-Type" = "application/json" }
$embeddingsUrl = "$AoaiEndpoint/openai/deployments/$EmbeddingDeployment/embeddings?api-version=$aoaiApiVersion"

$searchDataToken = Get-AadToken -Resource "https://search.azure.com"
$searchDataHeaders = @{ Authorization = "Bearer $searchDataToken"; "Content-Type" = "application/json" }

$batchSize = 16
$batchNum = 0
for ($i = $StartAtDocument; $i -lt $documents.Count; $i += $batchSize) {
    $batch = $documents[$i..([Math]::Min($i + $batchSize - 1, $documents.Count - 1))]

    # Refresh both AAD tokens periodically so long retry-extended runs don't fail on token expiry.
    if ($batchNum % 25 -eq 0) {
        $aoaiHeaders["Authorization"] = "Bearer $(Get-AadToken -Resource 'https://cognitiveservices.azure.com')"
        $searchDataHeaders["Authorization"] = "Bearer $(Get-AadToken -Resource 'https://search.azure.com')"
    }
    $batchNum++

    $embedBody = @{ input = @($batch.content) } | ConvertTo-Json -Depth 5
    $embedResponse = Invoke-RestMethodWithRetry -Method Post -Uri $embeddingsUrl -Headers $aoaiHeaders -Body $embedBody

    $uploadDocs = for ($j = 0; $j -lt $batch.Count; $j++) {
        $doc = $batch[$j]
        [pscustomobject]@{
            "@search.action" = "mergeOrUpload"
            id               = $doc.id
            content          = $doc.content
            contentVector    = $embedResponse.data[$j].embedding
            pdNbr            = $doc.pdNbr
            positionTitle    = $doc.positionTitle
            orgDesc          = $doc.orgDesc
            bureauDesc       = $doc.bureauDesc
            grdCode          = $doc.grdCode
            gvtOccSeries     = $doc.gvtOccSeries
            schedulePcInd    = $doc.schedulePcInd
        }
    }

    $uploadBody = @{ value = $uploadDocs } | ConvertTo-Json -Depth 10
    Invoke-RestMethodWithRetry -Method Post -Uri "$searchEndpoint/indexes/$SearchIndexName/docs/index?api-version=$searchApiVersion" -Headers $searchDataHeaders -Body $uploadBody | Out-Null

    Write-Host "  Uploaded $([Math]::Min($i + $batchSize, $documents.Count)) / $($documents.Count)" -ForegroundColor DarkGray
}

Write-Host "Registering Foundry Knowledge asset..." -ForegroundColor Cyan

$mlIndexDir = Join-Path $env:TEMP "hr-knowledge-mlindex"
New-Item -ItemType Directory -Path $mlIndexDir -Force | Out-Null

@"
`$schema: https://azuremlschemas.azureedge.net/latest/MLIndex.schema.json
name: $SearchIndexName
embeddings:
    api_type: azure_open_ai
    connection: $AoaiConnectionName
    deployment: $EmbeddingDeployment
    dimension: $EmbeddingDimensions
index:
    kind: acs
    connection: $SearchConnectionName
    index: $SearchIndexName
    field_mapping:
        content: content
        embedding: contentVector
"@ | Set-Content -Path (Join-Path $mlIndexDir "MLIndex.yaml")

az ml index create --name $SearchIndexName --version 1 --path $mlIndexDir --resource-group $ResourceGroup --workspace-name $ProjectName | Out-Null

Write-Host ""
Write-Host "Done. '$SearchIndexName' is now registered as Foundry Knowledge on $ProjectName." -ForegroundColor Green
