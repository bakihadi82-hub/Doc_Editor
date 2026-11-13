using PdfStudio.Core.Annotations;
using PdfStudio.Core.Documents;
using PdfStudio.Core.Forms;

namespace PdfStudio.Core.Editing;

/// <summary>
/// Represents a single page slot in the staged structural editing plan.
/// SourcePageIndex == null indicates a newly inserted blank page.
/// </summary>
public sealed record StagedPageEntry(
    int? SourcePageIndex,
    int AddedRotationQuarterTurns = 0,
    float BaseWidthPoints = 612f,
    float BaseHeightPoints = 792f,
    int BaseRotationDegrees = 0)
{
    public float BlankWidthPoints => BaseWidthPoints;
    public float BlankHeightPoints => BaseHeightPoints;
    public int TotalRotationQuarterTurns => (((BaseRotationDegrees / 90) + AddedRotationQuarterTurns) % 4 + 4) % 4;
    public int TotalRotationDegrees => TotalRotationQuarterTurns * 90;
    public float EffectiveWidthPoints => (TotalRotationQuarterTurns % 2 != 0) ? BaseHeightPoints : BaseWidthPoints;
    public float EffectiveHeightPoints => (TotalRotationQuarterTurns % 2 != 0) ? BaseWidthPoints : BaseHeightPoints;
}

/// <summary>
/// Non-destructive staged edits (reorder, rotate, delete, insert blank, annotations, form values) committed atomically on Save.
/// </summary>
public sealed class StructuralEditPlan
{
    public List<StagedPageEntry> Entries { get; } = [];
    public List<PdfAnnotation> Annotations { get; } = [];
    public List<PdfFormField> FormFields { get; } = [];
    public bool HasChanges { get; set; }

    public void ResetFromPageCount(int pageCount)
    {
        Entries.Clear();
        Annotations.Clear();
        FormFields.Clear();
        for (int i = 0; i < pageCount; i++)
        {
            Entries.Add(new StagedPageEntry(i));
        }
        HasChanges = false;
    }

    public void ResetFromPages(IReadOnlyList<PdfPageInfo> pages)
    {
        Entries.Clear();
        Annotations.Clear();
        FormFields.Clear();
        foreach (var p in pages)
        {
            Entries.Add(new StagedPageEntry(
                SourcePageIndex: p.EffectiveSourcePageIndex,
                AddedRotationQuarterTurns: 0,
                BaseWidthPoints: p.WidthPoints,
                BaseHeightPoints: p.HeightPoints,
                BaseRotationDegrees: p.RotationDegrees));
        }
        HasChanges = false;
    }

    public StructuralEditPlan Clone()
    {
        var copy = new StructuralEditPlan { HasChanges = HasChanges };
        copy.Entries.AddRange(Entries);
        foreach (var ann in Annotations)
        {
            copy.Annotations.Add(ann.Clone());
        }
        foreach (var field in FormFields)
        {
            copy.FormFields.Add(field.Clone());
        }
        return copy;
    }

    public void RestoreFrom(StructuralEditPlan snapshot)
    {
        Entries.Clear();
        Entries.AddRange(snapshot.Entries);

        Annotations.Clear();
        foreach (var ann in snapshot.Annotations)
        {
            Annotations.Add(ann.Clone());
        }

        FormFields.Clear();
        foreach (var field in snapshot.FormFields)
        {
            FormFields.Add(field.Clone());
        }

        HasChanges = snapshot.HasChanges;
    }

    public void RotateAt(int displayIndex, int quarterTurnsDelta)
    {
        if (displayIndex < 0 || displayIndex >= Entries.Count) return;
        var entry = Entries[displayIndex];
        int nextAdded = ((entry.AddedRotationQuarterTurns + quarterTurnsDelta) % 4 + 4) % 4;
        Entries[displayIndex] = entry with { AddedRotationQuarterTurns = nextAdded };
        HasChanges = true;
    }

    public bool DeleteAt(int displayIndex)
    {
        if (Entries.Count <= 1 || displayIndex < 0 || displayIndex >= Entries.Count)
        {
            return false;
        }
        Entries.RemoveAt(displayIndex);

        for (int i = Annotations.Count - 1; i >= 0; i--)
        {
            if (Annotations[i].PageIndex == displayIndex)
            {
                Annotations.RemoveAt(i);
            }
            else if (Annotations[i].PageIndex > displayIndex)
            {
                Annotations[i].PageIndex--;
            }
        }

        for (int i = FormFields.Count - 1; i >= 0; i--)
        {
            if (FormFields[i].PageIndex == displayIndex)
            {
                FormFields.RemoveAt(i);
            }
            else if (FormFields[i].PageIndex > displayIndex)
            {
                FormFields[i].PageIndex--;
            }
        }

        HasChanges = true;
        return true;
    }

    public bool Move(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Entries.Count || toIndex < 0 || toIndex >= Entries.Count || fromIndex == toIndex)
        {
            return false;
        }
        var item = Entries[fromIndex];
        Entries.RemoveAt(fromIndex);
        Entries.Insert(toIndex, item);

        foreach (var ann in Annotations)
        {
            ann.PageIndex = RemapMovedPageIndex(ann.PageIndex, fromIndex, toIndex);
        }

        foreach (var field in FormFields)
        {
            field.PageIndex = RemapMovedPageIndex(field.PageIndex, fromIndex, toIndex);
        }

        HasChanges = true;
        return true;
    }

    public int InsertBlankAfter(int displayIndex, float widthPt = 612f, float heightPt = 792f)
    {
        int insertIndex = Math.Clamp(displayIndex + 1, 0, Entries.Count);
        Entries.Insert(insertIndex, new StagedPageEntry(
            SourcePageIndex: null,
            AddedRotationQuarterTurns: 0,
            BaseWidthPoints: widthPt > 0 ? widthPt : 612f,
            BaseHeightPoints: heightPt > 0 ? heightPt : 792f,
            BaseRotationDegrees: 0));

        foreach (var ann in Annotations)
        {
            if (ann.PageIndex >= insertIndex)
            {
                ann.PageIndex++;
            }
        }

        foreach (var field in FormFields)
        {
            if (field.PageIndex >= insertIndex)
            {
                field.PageIndex++;
            }
        }

        HasChanges = true;
        return insertIndex;
    }

    private static int RemapMovedPageIndex(int current, int fromIndex, int toIndex)
    {
        if (current == fromIndex) return toIndex;
        if (fromIndex < toIndex)
        {
            if (current > fromIndex && current <= toIndex) return current - 1;
        }
        else
        {
            if (current >= toIndex && current < fromIndex) return current + 1;
        }
        return current;
    }

    public IReadOnlyList<PdfPageInfo> BuildPageInfos()
    {
        var result = new List<PdfPageInfo>(Entries.Count);
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            result.Add(new PdfPageInfo(
                PageIndex: i,
                WidthPoints: e.EffectiveWidthPoints,
                HeightPoints: e.EffectiveHeightPoints,
                RotationDegrees: e.TotalRotationDegrees,
                SourcePageIndex: e.SourcePageIndex ?? i,
                IsBlankPage: e.SourcePageIndex is null));
        }
        return result;
    }
}
