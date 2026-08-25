# Semantic Chat Data and Chart Confirmation Design

## Goal

Make HR query results understandable and consistent by keeping the AI's explanation and data grid in the chat, while reserving the results panel for charts that the user explicitly confirms.

## User Experience

Every successful data response has one primary presentation in the chat:

- A natural-language answer.
- A semantic data table using the real column names returned by the tool.
- When the data is chartable, a chart suggestion naming the proposed X and Y columns and offering an explicit confirmation action.

The results panel is empty until the user confirms a chart. After confirmation it shows only the chart, its title, and the selected X-axis and Y-axis columns. It never shows the transport fields `Label`, `Value`, or `Category` as generic user-facing headers.

Listing results may contain any number of columns. The chat table should remain usable in the constrained center panel; wide datasets use horizontal scrolling or a dedicated expandable data view rather than forcing the chart panel to render a generic three-column table.

Non-chartable results remain fully useful in chat and do not offer a chart action. A chart suggestion is generated only when there is one categorical or temporal dimension suitable for X and one numeric dimension suitable for Y.

## Architecture

Introduce a semantic dataset model alongside the existing `HrMetricRow` compatibility model:

```text
DataSet
  Title
  Summary
  Columns[]
    Name
    Type
  Rows[]
    Values: column name -> value
  ChartRecommendation?
    IsChartable
    XAxisColumn
    YAxisColumn
    Reason
```

The agent's final response contract will carry one serialized dataset payload plus the natural-language summary. The parser converts that payload into a `DataSet` without requiring fixed `label`, `value`, or `category` keys. Existing persisted `MetricsJson` rows continue to load through a compatibility adapter and are displayed using their existing header overrides.

`MessageViewModel` owns the dataset and chart recommendation for one assistant response. `ChatSessionService` owns the confirmed chart state. `ChatThread` renders summaries, grids, and chart-confirmation actions. `ResultsPanel` renders only the confirmed chart and its axis metadata.

## Data Flow

1. The agent calls one or more MCP tools according to the existing tool-use rules.
2. The model emits a semantic dataset payload when a tool returned HR data, followed by a concise summary.
3. The parser validates column definitions, row values, and any chart recommendation. Invalid or incomplete chart recommendations are discarded without discarding valid tabular data.
4. `ChatSessionService` stores the parsed dataset on the assistant message and persists the dataset JSON with the message.
5. `ChatThread` shows the summary and data grid. If a valid recommendation exists, it shows the proposed axes and a `Show chart` action.
6. On confirmation, the service records the selected dataset and axes as the current chart, then notifies `ResultsPanel`.
7. `ResultsPanel` renders the selected chart only. A new data response clears the confirmed chart until the user confirms the new recommendation.

## Compatibility and Error Handling

- Existing conversations with `MetricsJson` are read through a compatibility conversion to a two-dimensional semantic dataset.
- Missing, malformed, or type-inconsistent model fields must not crash the Blazor circuit. The dataset parser should coerce scalar values where reasonable and skip invalid rows or recommendations while retaining valid rows.
- A dataset with no valid chart recommendation is rendered as a grid only.
- A genuine empty result remains a valid dataset and is displayed as an empty result with the AI's summary; it is not treated as parser failure.
- Chart confirmation must validate that both selected axis columns exist and that the Y-axis values are numeric before changing the results panel.
- Persisted assistant content remains human-readable; serialized dataset payloads are stored separately and are not shown verbatim in chat.

## Testing

- Agent/parser tests cover arbitrary column names, many-column listings, numeric and text values, chartable and non-chartable datasets, malformed recommendations, and legacy `HrMetricRow` payloads.
- Service tests cover persistence, loading old and new conversations, chart confirmation, invalid axis confirmation, and clearing a previous chart when new data arrives.
- Component tests or focused rendering tests verify that chat uses semantic column headers, non-chartable data has no chart action, and the results panel does not render a grid or chart before confirmation.
- Existing provider parity and E2E tests remain passing.
