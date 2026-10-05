using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>Where a card is referenced from: an element of a whiteboard.</summary>
public class WbCardLink
{
    public string BoardId { get; set; } = "";
    public string BoardName { get; set; } = "";
    public string ElementId { get; set; } = "";
    /// <summary>Text of the element, or its kind ("Rectangle 2").</summary>
    public string Label { get; set; } = "";
}

/// <summary>
/// References from whiteboard elements to Kanban cards and to documents. The links are stored on the
/// elements (a whiteboard element points to tasks and documents, never the other way round), and a
/// task can be linked to one element only: linking it again replaces the previous link.
/// </summary>
public static class WhiteboardLinks
{
    /// <summary>Top-level elements and the elements merged inside groups.</summary>
    private static IEnumerable<WbElement> All(IEnumerable<WbElement> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            if (element.Children is null) continue;
            foreach (var child in All(element.Children)) yield return child;
        }
    }

    public static WbElement? ElementOfCard(WhiteboardData data, string cardId) =>
        data.Elements.FirstOrDefault(e => e.CardRefs?.Contains(cardId) == true);

    /// <summary>Removes the card from every element of the board. Returns true when something changed.</summary>
    public static bool UnlinkCard(WhiteboardData data, string cardId)
    {
        var changed = false;
        foreach (var element in All(data.Elements))
        {
            if (element.CardRefs is null || !element.CardRefs.Remove(cardId)) continue;
            if (element.CardRefs.Count == 0) element.CardRefs = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>Links a card to an element of this board, replacing any link the card had on this board.</summary>
    public static bool LinkCard(WhiteboardData data, string elementId, string cardId)
    {
        var element = WhiteboardOps.Find(data, elementId);
        if (element is null) return false;
        if (element.CardRefs?.Contains(cardId) == true) return false;
        UnlinkCard(data, cardId);
        (element.CardRefs ??= new List<string>()).Add(cardId);
        return true;
    }

    public static bool LinkDoc(WhiteboardData data, string elementId, string docPath)
    {
        var element = WhiteboardOps.Find(data, elementId);
        if (element is null) return false;
        if (element.DocRefs?.Contains(docPath, StringComparer.OrdinalIgnoreCase) == true) return false;
        (element.DocRefs ??= new List<string>()).Add(docPath);
        return true;
    }

    public static bool UnlinkDoc(WhiteboardData data, string elementId, string docPath)
    {
        var element = WhiteboardOps.Find(data, elementId);
        if (element?.DocRefs is null) return false;
        var removed = element.DocRefs.RemoveAll(p => p.Equals(docPath, StringComparison.OrdinalIgnoreCase)) > 0;
        if (element.DocRefs.Count == 0) element.DocRefs = null;
        return removed;
    }

    /// <summary>Links a task to an element only when no element of the board holds it (used when pasting a cut element).</summary>
    public static bool LinkFreeCard(WhiteboardData data, WbElement element, string cardId)
    {
        if (All(data.Elements).Any(e => e.CardRefs?.Contains(cardId) == true)) return false;
        (element.CardRefs ??= new List<string>()).Add(cardId);
        return true;
    }

    /// <summary>
    /// Removes the card from the elements of every other whiteboard of the project (on disk).
    /// Called after linking the card on <paramref name="exceptBoardId"/>.
    /// </summary>
    public static void UnlinkCardElsewhere(WhiteboardService service, string cardId, string exceptBoardId)
    {
        foreach (var info in service.List())
        {
            if (info.Id == exceptBoardId) continue;
            try
            {
                var data = service.Load(info.Id);
                if (data != null && UnlinkCard(data, cardId)) service.Save(data);
            }
            catch { /* an unreadable board cannot hold a usable link */ }
        }
    }

    /// <summary>Removes deleted cards from the elements of the whiteboards stored on disk (all but one).</summary>
    public static void UnlinkCardsOnDisk(WhiteboardService service, ICollection<string> cardIds, string? exceptBoardId)
    {
        if (cardIds.Count == 0) return;
        foreach (var info in service.List())
        {
            if (info.Id == exceptBoardId) continue;
            try
            {
                var data = service.Load(info.Id);
                if (data is null) continue;
                var changed = false;
                foreach (var id in cardIds) changed |= UnlinkCard(data, id);
                if (changed) service.Save(data);
            }
            catch { /* skip unreadable boards */ }
        }
    }

    /// <summary>The tasks linked on the other whiteboards of the project (on disk).</summary>
    public static HashSet<string> CardsLinkedOnDisk(WhiteboardService service, string? exceptBoardId)
    {
        var result = new HashSet<string>();
        foreach (var info in service.List())
        {
            if (info.Id == exceptBoardId) continue;
            try
            {
                if (service.Load(info.Id) is not { } data) continue;
                foreach (var element in data.Elements)
                    if (element.CardRefs != null) result.UnionWith(element.CardRefs);
            }
            catch { /* skip unreadable boards */ }
        }
        return result;
    }

    /// <summary>
    /// Undo / redo bring back an older state of a board, but references are not part of the history
    /// (they involve the tasks, the documents and the other boards): the elements that exist in both
    /// states keep the references they have now. An element that comes back from the past (an undone
    /// delete) keeps the ones it had, except the tasks that were linked somewhere else in the meantime.
    /// </summary>
    public static void CarryReferences(WhiteboardData current, WhiteboardData restored, Func<HashSet<string>> linkedOnOtherBoards)
    {
        var now = new Dictionary<string, WbElement>();
        foreach (var element in All(current.Elements)) now.TryAdd(element.Id, element);

        foreach (var element in All(restored.Elements))
        {
            if (!now.TryGetValue(element.Id, out var live)) continue;
            element.CardRefs = live.CardRefs?.ToList();
            element.DocRefs = live.DocRefs?.ToList();
        }

        // One element per task: those that never left come first, the returning ones give way.
        var taken = new HashSet<string>();
        HashSet<string>? elsewhere = null;
        foreach (var top in restored.Elements.OrderBy(e => now.ContainsKey(e.Id) ? 0 : 1).ToList())
        {
            if (top.CardRefs is null) continue;
            var back = !now.ContainsKey(top.Id);
            foreach (var cardId in top.CardRefs.ToList())
            {
                var free = taken.Add(cardId) && !(back && (elsewhere ??= linkedOnOtherBoards()).Contains(cardId));
                if (free) continue;
                foreach (var element in All(new[] { top }))
                {
                    if (element.CardRefs is null || !element.CardRefs.Remove(cardId)) continue;
                    if (element.CardRefs.Count == 0) element.CardRefs = null;
                }
            }
        }
    }

    /// <summary>Removes the references to a deleted document or folder. Returns true when something changed.</summary>
    public static bool RemoveDocs(List<string>? references, string path) =>
        references != null && references.RemoveAll(r => r.Equals(path, StringComparison.OrdinalIgnoreCase)
                                                        || r.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) > 0;

    public static bool RemoveDocs(WhiteboardData data, string path)
    {
        var changed = false;
        foreach (var element in All(data.Elements))
        {
            if (!RemoveDocs(element.DocRefs, path)) continue;
            if (element.DocRefs!.Count == 0) element.DocRefs = null;
            changed = true;
        }
        return changed;
    }

    public static bool RemoveDocs(BoardData board, string path)
    {
        var changed = false;
        foreach (var card in board.Columns.SelectMany(c => c.Cards).Concat(board.Archived))
        {
            if (!RemoveDocs(card.DocRefs, path)) continue;
            if (card.DocRefs!.Count == 0) card.DocRefs = null;
            changed = true;
        }
        return changed;
    }

    public static void RemoveDocsOnDisk(WhiteboardService service, string path, string? exceptBoardId)
    {
        foreach (var info in service.List())
        {
            if (info.Id == exceptBoardId) continue;
            try
            {
                var data = service.Load(info.Id);
                if (data != null && RemoveDocs(data, path)) service.Save(data);
            }
            catch { /* skip unreadable boards */ }
        }
    }

    /// <summary>
    /// card id -> the whiteboard element that references it, over every whiteboard of the project.
    /// <paramref name="live"/> is the board being edited: its in-memory state wins over the file.
    /// </summary>
    public static Dictionary<string, WbCardLink> CardIndex(WhiteboardService service, WhiteboardData? live = null)
    {
        var result = new Dictionary<string, WbCardLink>();
        var boards = new List<WhiteboardData>();
        if (live != null) boards.Add(live);
        foreach (var info in service.List())
        {
            if (live != null && info.Id == live.Id) continue;
            try
            {
                if (service.Load(info.Id) is { } data) boards.Add(data);
            }
            catch { /* skip unreadable boards */ }
        }

        foreach (var board in boards)
        {
            var counters = new Dictionary<string, int>();
            foreach (var element in board.Elements)
            {
                var label = WhiteboardGraph.LabelOf(element, counters);
                if (element.CardRefs is null) continue;
                foreach (var cardId in element.CardRefs)
                    result.TryAdd(cardId, new WbCardLink { BoardId = board.Id, BoardName = board.Name, ElementId = element.Id, Label = label });
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ documents renamed or moved

    /// <summary>
    /// The new path of a reference after <paramref name="oldPath"/> (a document or a folder) became
    /// <paramref name="newPath"/>; null when the reference is not affected.
    /// </summary>
    public static string? Remap(string reference, string oldPath, string newPath)
    {
        if (reference.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) return newPath;
        if (reference.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) return newPath + reference[oldPath.Length..];
        return null;
    }

    /// <summary>Updates a list of document references in place. Returns true when something changed.</summary>
    public static bool Remap(List<string>? references, string oldPath, string newPath)
    {
        if (references is null) return false;
        var changed = false;
        for (var i = 0; i < references.Count; i++)
        {
            if (Remap(references[i], oldPath, newPath) is not { } mapped) continue;
            references[i] = mapped;
            changed = true;
        }
        return changed;
    }

    /// <summary>Follows a renamed / moved document in the references of a whiteboard.</summary>
    public static bool RemapDocs(WhiteboardData data, string oldPath, string newPath)
    {
        var changed = false;
        foreach (var element in All(data.Elements)) changed |= Remap(element.DocRefs, oldPath, newPath);
        return changed;
    }

    /// <summary>Follows a renamed / moved document in the whiteboards stored on disk (all but one).</summary>
    public static void RemapDocsOnDisk(WhiteboardService service, string oldPath, string newPath, string? exceptBoardId)
    {
        foreach (var info in service.List())
        {
            if (info.Id == exceptBoardId) continue;
            try
            {
                var data = service.Load(info.Id);
                if (data != null && RemapDocs(data, oldPath, newPath)) service.Save(data);
            }
            catch { /* skip unreadable boards */ }
        }
    }

    /// <summary>Follows a renamed / moved document in the references of the cards.</summary>
    public static bool RemapDocs(BoardData board, string oldPath, string newPath)
    {
        var changed = false;
        foreach (var card in board.Columns.SelectMany(c => c.Cards).Concat(board.Archived))
            changed |= Remap(card.DocRefs, oldPath, newPath);
        return changed;
    }
}
