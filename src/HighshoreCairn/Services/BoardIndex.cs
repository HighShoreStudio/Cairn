using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>
/// Read-only lookup tables over a board, rebuilt whenever the board changes:
/// card by id, the column of each card, and the parent → subtasks relation.
/// It also computes the completion percentage shown by the progress circle.
/// </summary>
public class BoardIndex
{
    private readonly Dictionary<string, CardData> _cards = new();
    private readonly Dictionary<string, ColumnData> _columnOf = new();
    private readonly Dictionary<string, List<CardData>> _children = new();
    private readonly Dictionary<string, double> _progress = new();

    public BoardIndex(BoardData board)
    {
        foreach (var column in board.Columns)
        {
            foreach (var card in column.Cards)
            {
                _cards[card.Id] = card;
                _columnOf[card.Id] = column;
            }
        }

        // Only links between cards that are on the board count (an archived parent is ignored).
        foreach (var card in _cards.Values)
        {
            if (card.ParentId is null || card.ParentId == card.Id || !_cards.ContainsKey(card.ParentId)) continue;
            if (!_children.TryGetValue(card.ParentId, out var list)) _children[card.ParentId] = list = new List<CardData>();
            list.Add(card);
        }
    }

    public IEnumerable<CardData> Cards => _cards.Values;

    public CardData? Find(string? id) => id != null && _cards.TryGetValue(id, out var card) ? card : null;

    public ColumnData? ColumnOf(string id) => _columnOf.TryGetValue(id, out var column) ? column : null;

    public bool IsInDoneColumn(string id) => ColumnOf(id)?.IsDone == true;

    /// <summary>The parent task, when it exists on the board.</summary>
    public CardData? ParentOf(CardData card) => card.ParentId == card.Id ? null : Find(card.ParentId);

    public IReadOnlyList<CardData> ChildrenOf(string id) =>
        _children.TryGetValue(id, out var list) ? list : Array.Empty<CardData>();

    /// <summary>True when <paramref name="candidateId"/> is somewhere below <paramref name="ancestorId"/>.</summary>
    public bool IsDescendant(string candidateId, string ancestorId)
    {
        var guard = 0;
        var current = Find(candidateId);
        while (current?.ParentId != null && guard++ < 1000)
        {
            if (current.ParentId == ancestorId) return true;
            current = Find(current.ParentId);
        }
        return false;
    }

    /// <summary>A card can become a subtask of <paramref name="parentId"/> unless that would create a loop.</summary>
    public bool CanBeChildOf(string cardId, string parentId) =>
        cardId != parentId && !IsDescendant(parentId, cardId);

    /// <summary>
    /// Completion between 0 and 1. A card in a "done" column is 100%. Otherwise every checklist
    /// item and every subtask is one unit: a checked item counts 1, a subtask counts its own progress.
    /// </summary>
    public double Progress(CardData card) => Progress(card, new HashSet<string>());

    private double Progress(CardData card, HashSet<string> visiting)
    {
        if (_progress.TryGetValue(card.Id, out var cached)) return cached;
        if (!visiting.Add(card.Id)) return 0; // corrupted data with a loop: stop here

        double result;
        if (IsInDoneColumn(card.Id))
        {
            result = 1;
        }
        else
        {
            var children = ChildrenOf(card.Id);
            var units = card.Checklist.Count + children.Count;
            if (units == 0)
            {
                result = 0;
            }
            else
            {
                double done = card.Checklist.Count(i => i.Done);
                foreach (var child in children) done += Progress(child, visiting);
                result = done / units;
            }
        }

        visiting.Remove(card.Id);
        _progress[card.Id] = result;
        return result;
    }

    /// <summary>Subtasks counted as finished (100% progress) / total subtasks.</summary>
    public (int Done, int Total) SubtaskCount(string id)
    {
        var children = ChildrenOf(id);
        return (children.Count(c => Progress(c) >= 0.999), children.Count);
    }
}
