# Task 12 Brief: ResultsPanel component

## Context
Task 12 of 13. Creates the right panel component and uncomments it in MainLayout.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Tasks 1-11 complete.

Key interfaces:
- `ChatSessionService.CurrentMetrics: IReadOnlyList<HrMetricRow>` — subscribe to `OnChange`
- `ChatSessionService.IsStreaming: bool`
- `HrMetricRow` — in `HrDashboard.Agents.Models`, properties: `Label (string)`, `Value (double)`, `Category (string?)`
- `ChartType`, `ChartSeries` — MudBlazor v8 chart types

## Global Constraints
- net10.0, MudBlazor v8
- No subagents — implement, build, commit, write report yourself

## File 1: Create `src/HrDashboard.Web/Components/Chat/ResultsPanel.razor`

```razor
@inject ChatSessionService Session
@implements IDisposable

@if (Session.CurrentMetrics.Count == 0)
{
    <div style="display:flex;flex-direction:column;align-items:center;justify-content:center;height:200px;opacity:0.4">
        <MudIcon Icon="@Icons.Material.Filled.BarChart" Style="font-size:48px" />
        <MudText Typo="Typo.body2" Class="mt-2">Results appear here</MudText>
    </div>
}
else
{
    <MudStack Row="true" AlignItems="AlignItems.Center" Justify="Justify.SpaceBetween" Class="mb-3">
        <MudText Typo="Typo.subtitle2">Visualization</MudText>
        <MudSelect T="ChartType" @bind-Value="_chartType" Dense="true"
                   Variant="Variant.Outlined" Style="width:130px">
            <MudSelectItem Value="ChartType.Bar">Bar</MudSelectItem>
            <MudSelectItem Value="ChartType.Donut">Donut</MudSelectItem>
            <MudSelectItem Value="ChartType.Line">Line</MudSelectItem>
        </MudSelect>
    </MudStack>

    <MudChart ChartType="_chartType"
              ChartSeries="@_chartSeries"
              XAxisLabels="@_chartLabels"
              Width="100%"
              Height="220px"
              Class="mb-4" />

    <MudText Typo="Typo.subtitle2" Class="mb-2">
        Data (@Session.CurrentMetrics.Count rows)
    </MudText>
    <MudDataGrid T="HrMetricRow"
                 Items="Session.CurrentMetrics"
                 Dense="true"
                 Striped="true"
                 Hover="true"
                 Filterable="false"
                 SortMode="SortMode.Single">
        <Columns>
            <PropertyColumn Property="r => r.Label"    Title="Label"    Sortable="true" />
            <PropertyColumn Property="r => r.Value"    Title="Value"    Sortable="true" Format="N2" />
            <PropertyColumn Property="r => r.Category" Title="Category" Sortable="true" />
        </Columns>
    </MudDataGrid>
}

@code {
    private ChartType _chartType = ChartType.Bar;
    private List<ChartSeries> _chartSeries = [];
    private string[] _chartLabels = [];

    protected override void OnInitialized()
    {
        Session.OnChange += OnSessionChange;
    }

    private void OnSessionChange()
    {
        BuildChart();
        InvokeAsync(StateHasChanged);
    }

    private void BuildChart()
    {
        if (Session.CurrentMetrics.Count == 0)
        {
            _chartSeries = [];
            _chartLabels = [];
            return;
        }

        _chartLabels = Session.CurrentMetrics
            .Select(r => r.Label.Length <= 18 ? r.Label : r.Label[..18] + "…")
            .ToArray();

        _chartSeries =
        [
            new ChartSeries
            {
                Name = "Value",
                Data = Session.CurrentMetrics.Select(r => r.Value).ToArray()
            }
        ];
    }

    public void Dispose() => Session.OnChange -= OnSessionChange;
}
```

## File 2: Uncomment ResultsPanel in MainLayout.razor

In `src/HrDashboard.Web/Components/Layout/MainLayout.razor`, find these lines:
```
@* TODO Task 12: uncomment when ResultsPanel is created *@
@* <ResultsPanel /> *@
```

Replace them with:
```razor
<ResultsPanel />
```

## Steps
1. Create `src/HrDashboard.Web/Components/Chat/ResultsPanel.razor` with exact content above
2. Uncomment ResultsPanel in MainLayout.razor (remove the 2 TODO comment lines, replace with active tag)
3. Run: `dotnet build src/HrDashboard.Web/HrDashboard.Web.csproj`
4. Verify: Build succeeded, 0 errors
5. Commit:
   ```
   git add src/HrDashboard.Web/Components/Chat/ResultsPanel.razor \
           src/HrDashboard.Web/Components/Layout/MainLayout.razor
   git commit -m "feat(web): add ResultsPanel with MudChart and MudDataGrid driven by ChatSessionService"
   ```
6. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-12-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result
Concerns: (if any)
