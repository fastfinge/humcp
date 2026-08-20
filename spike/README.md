# Phase 0 spike — throwaway

Three small programs whose only job is to answer one question: **is WPF's accessibility good
enough to build humcp on, or do we fall back to WinForms?** (ROADMAP §10, Phase 0.)

None of this is production code and none of it is referenced by `src/`. Delete the whole folder
once Phase 0 is signed off — the findings live in [`docs/phase0-findings.md`](../docs/phase0-findings.md).

## Running it

```bash
dotnet build spike/Humcp.Spike.slnx

# 1. The spike app itself — drive it with a screen reader. This is the real test.
dotnet run --project spike/Humcp.Spike

# 2. Mechanical UIA assertions. Launches the app, walks the live UIA tree, writes a report.
#    Takes over the foreground for ~30 seconds because it sends real Tab keystrokes.
dotnet run --project spike/Humcp.Spike.UiaProbe

# 3. prismatoid smoke test.
dotnet run --project spike/Humcp.Spike.PrismSmoke -- --braille
dotnet run --project spike/Humcp.Spike.PrismSmoke -- --quiet    # enumerate, make no sound
```

## What the spike app contains

- A **four-level tree** (server → category → group → tool), plus a three-level branch, because the
  bug that ruled out WinUI 3 was flattened tree levels.
- A **generated form** built by `DataTemplateSelector` over a field model — the mechanism Phase 3
  will use — covering every Tier 1 control, plus one Tier 2 `GroupBox`.
- The parts of the §7 contract that can fail silently: `IsRequiredForForm`, `HelpText`,
  `LabeledBy`, `ItemStatus`, an error summary list, focus-to-first-invalid, a read-only multiline
  result box, and runtime access-key allocation.
- Deliberate experiments: **View → Virtualize the generated form** (proves §7.6), **Add 200
  fields** (tab-order stress), **Add a field** (dynamic-addition announcements), and a
  DatePicker/plain-ISO-text-box pair for the same semantic field.
- **Speech menu** for prism speak / braille / both, and **File → prism diagnostics** for the
  backend registry.

Keyboard: <kbd>F6</kbd> panes, <kbd>Ctrl+F</kbd> filter, <kbd>Ctrl+Enter</kbd> run,
<kbd>F1</kbd> details, <kbd>Esc</kbd> closes a details window.
