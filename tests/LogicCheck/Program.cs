using HighshoreCairn.Models;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what); if (!ok) failures++; }

var root = Path.Combine(Path.GetTempPath(), "hk-test-" + Guid.NewGuid().ToString("N"));
AppPaths.OverrideRoot(root);
var dialogs = new FakeDialogs();

Console.WriteLine("== accounts");
var accounts = new AccountService(new XorProtector());
accounts.Load();
var luca = accounts.Create("Luca", "secret1", "luca@example.com", "#1F6F8B");
accounts.StartSession(luca);
Check(File.Exists(AppPaths.AccountsFile), "accounts.json written");
Check(!File.ReadAllText(AppPaths.AccountsFile).Contains("Luca"), "accounts.json does not contain plain text");
Check(accounts.VerifyPassword(luca, "secret1") && !accounts.VerifyPassword(luca, "secret2"), "password verify");
Check(luca.PasswordHash != "secret1" && luca.Iterations >= 100000, "password hashed (PBKDF2)");
try { accounts.Create("luca", "secret1", null, "#000000"); Check(false, "duplicate username rejected"); } catch (InvalidOperationException) { Check(true, "duplicate username rejected"); }
try { accounts.Create("Anna", "123", null, "#000000"); Check(false, "short password rejected"); } catch (InvalidOperationException) { Check(true, "short password rejected"); }

var accounts2 = new AccountService(new XorProtector());
accounts2.Load();
Check(accounts2.Accounts.Count == 1, "accounts reloaded from encrypted file");
Check(accounts2.TryAutoLogin()?.Username == "Luca", "auto-login from session token");
accounts2.Logout();
var accounts3 = new AccountService(new XorProtector());
accounts3.Load();
Check(accounts3.TryAutoLogin() is null, "no auto-login after logout");
Check(accounts3.Login("LUCA", "secret1") != null, "login (case-insensitive username)");
Check(accounts3.Login("Luca", "wrong") is null, "login with wrong password fails");
accounts = accounts3;
luca = accounts.Current!;
var anna = accounts.Create("Anna Rossi", "password2", null, "#E5484D");
Check(anna.Initials == "AR" && luca.Initials == "LU", "initials");

Console.WriteLine("== projects");
var projects = new ProjectService();
Check(ProjectService.Slug("My Game! 2") == "my-game-2", "slug");
var entry = projects.Create("My Game", "desc", null, "#1F6F8B", luca);
foreach (var f in new[] { "project.json", "kanban.json", "accounts.json", ".gitkeep" })
    Check(File.Exists(Path.Combine(entry.Folder, f)), f + " created");
Check(Directory.Exists(Path.Combine(entry.Folder, "backups")), "backups/ created");
Check(entry.Folder.EndsWith(Path.Combine("projects", "my-game")), "default folder = projects/{slug}");
Check(!File.ReadAllText(Path.Combine(entry.Folder, "accounts.json")).Contains("sessionTokenHashes"), "project accounts have no session tokens");
Check(projects.List().Count == 1, "project listed");
try { projects.Create("My Game", "", null, "#1F6F8B", luca); Check(false, "duplicate project rejected"); } catch (InvalidOperationException) { Check(true, "duplicate project rejected"); }

Console.WriteLine("== board");
var main = new MainViewModel(accounts, projects, dialogs);
main.Start();
Check(main.CurrentView is ProjectListViewModel, "auto-login -> project list");
main.OpenProject(entry);
var board = main.Board!;
Check(board != null && board.Columns.Count == 3, "board opened with 3 default columns");
Check(board!.Columns.Select(c => c.Name).SequenceEqual(new[] { "TODO", "In Progress", "Done" }), "default column names");

// create a card through the editor
dialogs.OnDialog = vm =>
{
    if (vm is CardEditorViewModel ed)
    {
        ed.Title = "Fix login bug";
        ed.Description = "Some **markdown** text";
        ed.Priority = Priority.High;
        ed.SelectedAssignee = ed.AssigneeOptions.First(o => o.Id == luca.Id);
        ed.TagOptions.First(t => t.Name == "Bug").IsSelected = true;
        ed.DueDate = DateTime.Today.AddDays(-1);
        ed.NewChecklistText = "step 1"; ed.AddChecklistItemCommand.Execute(null);
        ed.NewChecklistText = "step 2"; ed.AddChecklistItemCommand.Execute(null);
        ed.Checklist[0].Done = true;
        ed.NewCommentText = "first comment"; ed.PostCommentCommand.Execute(null);
        ed.SaveCommand.Execute(null);
        return ed.Result != null;
    }
    return false;
};
board.Columns[0].AddCardCommand.Execute(null);
Check(board.Columns[0].VisibleCards.Count == 1, "card created");
var card = board.Columns[0].VisibleCards[0];
Check(card.Data.CreatorId == luca.Id && card.Data.CreatorName == "Luca", "creator autofilled");
Check(card.Data.Activity.Any(a => a.Text == "created this card") && card.Data.Activity.Any(a => a.Text == "added a comment"), "activity: created + comment");
Check(card.IsOverdue && board.HasOverdue && board.DueSummary.Contains("1 overdue (1 assigned to you)"), "overdue notification: " + board.DueSummary);
Check(card.ChecklistText == "1/2" && card.Tags.Count == 1 && card.HasAssignee && card.CommentCount == "1", "card display values");

// second + third card
int n = 0;
dialogs.OnDialog = vm => { if (vm is CardEditorViewModel ed) { ed.Title = "Card " + (++n); ed.Priority = n == 1 ? Priority.Urgent : Priority.Low; ed.SaveCommand.Execute(null); return true; } return false; };
board.Columns[0].AddCardCommand.Execute(null);
board.Columns[0].AddCardCommand.Execute(null);
Check(board.Columns[0].CountText == "3", "count text");
var c1 = board.Columns[0].VisibleCards[1]; var c2 = board.Columns[0].VisibleCards[2];

// drag & drop: reorder inside column (c2 before first), then move to another column
board.MoveCard(c2, board.Columns[0], card, after: false);
Check(board.Data.Columns[0].Cards.Select(c => c.Title).SequenceEqual(new[] { "Card 2", "Fix login bug", "Card 1" }), "reorder inside column");
board.MoveCard(c2, board.Columns[0], c1, after: true);
Check(board.Data.Columns[0].Cards.Select(c => c.Title).SequenceEqual(new[] { "Fix login bug", "Card 1", "Card 2" }), "reorder after anchor");
board.MoveCard(card, board.Columns[2], null, false);
Check(board.Data.Columns[2].Cards.Count == 1 && card.Data.Activity.Any(a => a.Text == "moved this card from TODO to Done"), "move between columns + activity");
Check(!board.Columns[2].VisibleCards[0].IsOverdue && !board.HasOverdue, "cards in a done column are not overdue");
board.MoveCard(board.Columns[2].VisibleCards[0], board.Columns[1], null, false);

// persistence
var disk = projects.LoadBoard(entry.Folder);
Check(disk.Columns[1].Cards.Count == 1 && disk.Columns[0].Cards.Count == 2, "auto-save persisted moves");
var json = File.ReadAllText(Path.Combine(entry.Folder, "kanban.json"));
Check(json.Contains("\"priority\": \"High\"") && json.Contains("\n  ") && !json.Contains("dueMoment"), "kanban.json is indented, enums as strings");
Check(disk.Columns[1].Cards[0].DueDate == DateTime.Today.AddDays(-1) && !disk.Columns[1].Cards[0].DueHasTime, "due date roundtrip");

// inline edits
card = board.Columns[1].VisibleCards[0];
board.SetPriority(card, Priority.Urgent); board.SetAssignee(card, null); board.ToggleTag(card, board.Data.Tags[1].Id);
card = board.Columns[1].VisibleCards[0];
Check(card.Priority == Priority.Urgent && !card.HasAssignee && card.Tags.Count == 2, "inline priority/assignee/tag");
Check(card.PriorityChoices.Count == 5 && card.AssigneeChoices.Count == 2 && card.TagChoices.Count(t => t.IsChecked) == 2 && card.MoveChoices.Count == 3, "context menu choices");

// filters
board.Filter.SearchText = "login";
Check(board.Columns.Sum(c => c.VisibleCards.Count) == 1 && board.Columns[0].CountText == "0 / 2", "search filter");
board.Filter.Clear();
board.Filter.Priorities.First(p => p.Id == "Urgent").IsSelected = true;
Check(board.Columns.Sum(c => c.VisibleCards.Count) == 2 && board.Filter.PriorityHeader == "Priority (1)", "priority filter");
board.Filter.Clear();
board.Filter.Assignees.First(a => a.Id == "").IsSelected = true;
Check(board.Columns.Sum(c => c.VisibleCards.Count) == 3, "unassigned filter");
board.Filter.Clear();
board.Filter.Tags.First(t => t.Name == "Bug").IsSelected = true;
Check(board.Columns.Sum(c => c.VisibleCards.Count) == 1, "tag filter");
board.Filter.Clear();
board.Filter.Columns.First(c => c.Name == "Done").IsSelected = true;
Check(board.Columns.Count(c => c.IsVisible) == 1, "column filter");
board.Filter.Clear();
board.Filter.Due = DueFilter.Overdue;
Check(board.Columns.Sum(c => c.VisibleCards.Count) == 1, "overdue filter");
board.Filter.Due = DueFilter.NoDueDate;
Check(board.Columns.Sum(c => c.VisibleCards.Count) == 2, "no due date filter");
board.Filter.Clear();
board.Filter.Sort = SortMode.Priority;
Check(board.Columns[0].VisibleCards[0].Title == "Card 1", "sort by priority (urgent first)");
board.Filter.Reverse = true;
Check(board.Columns[0].VisibleCards[0].Title == "Card 2", "reverse sort");
board.Filter.Sort = SortMode.Manual; board.Filter.Reverse = false;
Check(FilterViewModel.StartOfWeek(new DateTime(2026, 10, 4, 15, 0, 0)) == new DateTime(2026, 9, 28), "start of week (Sunday -> Monday)");
var wk = new CardData { DueDate = new DateTime(2026, 10, 4) };
Check(FilterViewModel.MatchesDue(wk, DueFilter.ThisWeek, false, new DateTime(2026, 10, 2)) && !FilterViewModel.MatchesDue(wk, DueFilter.ThisWeek, false, new DateTime(2026, 10, 5)), "due this week");

// saved filter
board.Filter.SearchText = "card"; board.Filter.Sort = SortMode.Created;
dialogs.PromptAnswer = "My filter";
dialogs.OnDialog = vm => { if (vm is PromptViewModel p) { p.Value = dialogs.PromptAnswer; return true; } return false; };
board.SaveFilterCommand.Execute(null);
board.ClearFiltersCommand.Execute(null);
Check(board.Filter.SearchText == "" && board.SavedFilters.Count == 1, "filter saved + cleared");
board.SelectedSavedFilter = board.SavedFilters[0];
Check(board.Filter.SearchText == "card" && board.Filter.Sort == SortMode.Created, "saved filter applied");
board.ClearFiltersCommand.Execute(null); board.Filter.Sort = SortMode.Manual;

// columns
dialogs.PromptAnswer = "Review";
board.AddColumnCommand.Execute(null);
Check(board.Columns.Count == 4 && board.Columns[3].Name == "Review", "add column");
board.Columns[3].MoveLeftCommand.Execute(null);
Check(board.Data.Columns[2].Name == "Review", "reorder column");
dialogs.PromptAnswer = "QA";
board.Columns[2].RenameCommand.Execute(null);
Check(projects.LoadBoard(entry.Folder).Columns[2].Name == "QA", "rename column persisted");
board.Columns[2].DeleteCommand.Execute(null);
Check(board.Columns.Count == 3, "delete column");

// edit card (diff -> activity), comments permissions
card = board.Columns[1].VisibleCards[0];
dialogs.OnDialog = vm =>
{
    if (vm is CardEditorViewModel ed)
    {
        Check(!ed.IsDirty, "editor not dirty when opened");
        ed.Title = "Fix login bug (again)"; ed.DueTime = "18:30"; ed.Checklist[1].Done = true;
        ed.SelectedColumn = ed.Columns[2];
        Check(ed.IsDirty, "editor dirty after changes");
        Check(ed.Comments.Count == 1 && ed.Comments[0].CanDelete, "author can delete own comment");
        ed.SaveCommand.Execute(null); return true;
    }
    return false;
};
board.EditCard(card);
card = board.Columns[2].VisibleCards[0];
var acts = card.Data.Activity.Select(a => a.Text).ToList();
Check(card.Title == "Fix login bug (again)" && card.Data.DueHasTime && card.Data.DueDate!.Value.Hour == 18, "edit applied (title, due time)");
Check(acts.Any(a => a.StartsWith("renamed this card")) && acts.Any(a => a.StartsWith("set the due date")) && acts.Any(a => a.Contains("checklist (2/2 done)")) && acts.Last() == "moved this card from In Progress to Done", "edit activity log");

// archive / restore / delete
board.ArchiveCard(card);
Check(board.Data.Archived.Count == 1 && board.Columns[2].VisibleCards.Count == 0 && board.ArchiveText == "Archive (1)", "archive");
board.RestoreCard(board.Data.Archived[0]);
Check(board.Data.Archived.Count == 0 && board.Columns[2].VisibleCards.Count == 1, "restore to original column");
dialogs.ConfirmAnswer = true;
board.DeleteCard(board.Columns[0].VisibleCards[0]);
Check(board.Data.Columns[0].Cards.Count == 1, "delete card");

// backups
var backups = projects.ListBackups(entry.Folder);
Check(backups.Count >= 1 && backups.Count <= 5, $"backups created ({backups.Count})");
for (int i = 0; i < 8; i++) { board.Data.Columns[0].Name = "TODO" + i; board.Save(); File.Copy(Path.Combine(entry.Folder, "kanban.json"), Path.Combine(entry.Folder, "backups", $"kanban-2026-01-0{i + 1}T10-00-00.json"), true); }
board.Data.Columns[0].Name = "TODO"; board.Save();
projects.Backup(entry.Folder);
backups = projects.ListBackups(entry.Folder);
Check(backups.Count == 5, "only the last 5 backups are kept");
Check(backups[0].FileName.CompareTo(backups[4].FileName) > 0, "backups newest first");
var before = projects.ListBackups(entry.Folder).Count;
projects.Backup(entry.Folder);
Check(projects.ListBackups(entry.Folder).Count == before, "identical board is not backed up twice");
var old = backups.First(x => x.FileName.Contains("2026-01-08"));   // the newest of the hand-made backups (column named TODO7)
projects.RestoreBackup(entry.Folder, old);
Check(projects.LoadBoard(entry.Folder).Columns[0].Name == "TODO7", "restore backup");
board.Load();
Check(board.Columns[0].Name == "TODO7", "board reloaded after restore");

// tags manager
dialogs.OnDialog = vm => { if (vm is TagManagerViewModel tm) { tm.RemoveCommand.Execute(tm.Tags.First(t => t.Name == "Bug")); tm.AddCommand.Execute(null); tm.Selected!.Name = "Art"; tm.SaveCommand.Execute(null); return true; } return false; };
Check(board.ManageTags() && board.Data.Tags.Count == 3 && board.Data.Tags.Any(t => t.Name == "Art") && !board.Data.Columns.SelectMany(c => c.Cards).Any(c => c.TagIds.Any(id => board.FindTag(id) == null)), "tag manager (delete removes tag from cards)");

// ---------------------------------------------------------------- v1.1: dependencies, progress, column colors
Console.WriteLine("== subtasks & progress");
dialogs.OnDialog = vm => { if (vm is CardEditorViewModel ed) { ed.Title = "Epic"; ed.NewChecklistText = "a"; ed.AddChecklistItemCommand.Execute(null); ed.NewChecklistText = "b"; ed.AddChecklistItemCommand.Execute(null); ed.Checklist[0].Done = true; ed.NewSubtaskTitle = "Sub new"; ed.AddNewSubtaskCommand.Execute(null); ed.SaveCommand.Execute(null); return true; } return false; };
board.Columns[0].AddCardCommand.Execute(null);
var epic = board.Columns[0].VisibleCards.First(c => c.Title == "Epic");
var subNew = board.Columns[0].VisibleCards.First(c => c.Title == "Sub new");
Check(subNew.Data.ParentId == epic.Data.Id && subNew.HasParent && subNew.ParentTitle == "Epic", "new subtask created from the editor");
Check(epic.HasSubtasks && epic.SubtaskText == "0/1", "parent shows its subtasks");
Check(Math.Abs(epic.Progress - 1.0 / 3) < 1e-9, "progress = (1 checklist done + 0 subtask) / 3 units");
// link an existing card through the context menu
var other = board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title != "Epic" && c.Title != "Sub new");
board.SetParent(other, epic.Data.Id);
epic = board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title == "Epic");
Check(board.Index.ChildrenOf(epic.Data.Id).Count == 2 && Math.Abs(epic.Progress - (1 + board.Index.Progress(other.Data)) / 4) < 1e-9, "existing card linked as subtask");
// loops are refused
board.SetParent(epic, other.Data.Id);
Check(epic.Data.ParentId is null, "a parent cannot become a subtask of its own subtask");
Check(!epic.ParentChoices.Any(c => c.Label.StartsWith(MenuText.Escape(other.Title))) && subNew.ParentChoices.Count >= 2, "parent menu excludes descendants");
// moving a subtask to a done column completes it
subNew = board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title == "Sub new");
board.MoveCard(subNew, board.Columns.First(c => c.IsDone), null, false);
epic = board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title == "Epic");
subNew = board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title == "Sub new");
Check(subNew.IsCompleted && subNew.Progress == 1 && epic.SubtaskText.StartsWith("1/"), "subtask in a done column counts as complete");
// editor: remove a subtask, set parent, loop validation
dialogs.OnDialog = vm => { if (vm is CardEditorViewModel ed) { Check(ed.Subtasks.Count == 2 && ed.ParentOptions.All(o => o.Id != other.Data.Id), "editor lists subtasks; parent options exclude them"); ed.RemoveSubtaskCommand.Execute(ed.Subtasks.First(r => r.Id == other.Data.Id)); Check(ed.IsDirty && ed.SubtaskCandidates.Any(c => c.Id == other.Data.Id), "removed subtask becomes a candidate again"); ed.SaveCommand.Execute(null); return true; } return false; };
board.EditCard(epic);
Check(board.Index.ChildrenOf(epic.Data.Id).Count == 1 && other.Data.ParentId is null, "subtask unlinked from the editor");
var reloaded = projects.LoadBoard(entry.Folder);
Check(reloaded.Columns.SelectMany(c => c.Cards).First(c => c.Title == "Sub new").ParentId == epic.Data.Id, "parentId persisted");
Check(!File.ReadAllText(Path.Combine(entry.Folder, "kanban.json")).Contains("\"parentId\": null"), "null parentId is not written");
// deleting the parent frees the subtasks
dialogs.ConfirmAnswer = true;
board.DeleteCard(board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title == "Epic"));
Check(board.Data.Columns.SelectMany(c => c.Cards).First(c => c.Title == "Sub new").ParentId is null, "deleting the parent clears the link");
board.DeleteCard(board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Title == "Sub new"));

Console.WriteLine("== column colors");
dialogs.OnDialog = vm => { if (vm is ColumnStyleViewModel cs) { cs.Light.Background = "#E3F0F4"; cs.Light.TitleHex = "#16324F"; cs.SaveCommand.Execute(null); return true; } return false; };
board.Columns[0].EditStyleCommand.Execute(null);
Check(board.Columns[0].HasCustomBackground && board.Columns[0].BackgroundColor == "#E3F0F4" && board.Columns[0].TitleColor == "#16324F", "column colors set");
Check(projects.LoadBoard(entry.Folder).Columns[0].Background == "#E3F0F4", "column colors persisted");
dialogs.OnDialog = vm => { if (vm is ColumnStyleViewModel cs) { cs.Light.ResetBackgroundCommand.Execute(null); cs.Light.ResetTitleCommand.Execute(null); cs.SaveCommand.Execute(null); return true; } return false; };
board.Columns[0].EditStyleCommand.Execute(null);
Check(!board.Columns[0].HasCustomBackground && !board.Columns[0].HasCustomTitleColor && !File.ReadAllText(Path.Combine(entry.Folder, "kanban.json")).Contains("\"background\""), "column colors reset to default");

// ---------------------------------------------------------------- v1.1: whiteboard
Console.WriteLine("== whiteboard");
board.IsWhiteboardMode = true;
var wb = board.Whiteboard!;
Check(wb.Boards.Count == 1 && wb.Data.Name == "Main" && Directory.GetFiles(Path.Combine(entry.Folder, "whiteboards"), "board-*.json").Length == 1, "first whiteboard created on demand");
WbElement Rect(double x, double y, double w = 100, double h = 60) { var e = new WbElement { Kind = WbKind.Rectangle, X = x, Y = y, Width = w, Height = h }; wb.StyleNew(e); return e; }
var r1 = Rect(0, 0); var r2 = Rect(300, 0); var r3 = Rect(0, 300);
wb.AddElement(r1); wb.AddElement(r2); wb.AddElement(r3);
Check(wb.Data.Elements.Count == 3 && wb.SelectedIds.Single() == r3.Id && wb.CanUndo, "elements added, last one selected");
Check(wb.TryConnect(r1.Id, r2.Id) && !wb.TryConnect(r2.Id, r1.Id) && wb.Data.Connectors.Count == 1 && wb.StatusText.Contains("already"), "one connector per pair of elements");
Check(wb.Data.Connectors[0].Thickness == 1, "connectors are thin by default");
wb.ConnectorThickness = 3;
Check(wb.Data.Connectors[0].Thickness == 3, "thickness of the selected connector can be changed");
Check(wb.TryConnect(r2.Id, r3.Id), "second connector");
// geometry
var rot = new WbElement { Kind = WbKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 50, Rotation = 90 };
var b = WhiteboardOps.Bounds(rot);
Check(Math.Abs(b.Width - 50) < 1e-6 && Math.Abs(b.Height - 100) < 1e-6, "bounds of a rotated element");
var edge = WhiteboardOps.EdgePoint(r1, new WbPoint(1000, 30));
Check(Math.Abs(edge.X - 100) < 1e-6 && Math.Abs(edge.Y - 30) < 1e-6, "connector leaves the box on its edge");
var rs = new WbElement { Kind = WbKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 100 };
WhiteboardOps.Resize(rs, 1, 1, new WbPoint(200, 150), false);
Check(rs.X == 0 && rs.Y == 0 && rs.Width == 200 && rs.Height == 150, "resize from the bottom-right handle keeps the top-left corner");
var rr = new WbElement { Kind = WbKind.Rectangle, X = 0, Y = 0, Width = 100, Height = 100, Rotation = 90 };
var tl = WhiteboardOps.Corners(rr)[0];
WhiteboardOps.Resize(rr, 1, 1, WhiteboardOps.Corners(rr)[2] + new WbPoint(-40, 60), false);
var tl2 = WhiteboardOps.Corners(rr)[0];
Check(Math.Abs(tl.X - tl2.X) < 1e-6 && Math.Abs(tl.Y - tl2.Y) < 1e-6 && rr.Width > 100, "resize of a rotated element keeps the opposite corner fixed");
var pts = Enumerable.Range(0, 50).Select(i => new WbPoint(i * 2, i % 2 == 0 ? 0 : 0.2)).ToList();
Check(WhiteboardOps.Simplify(pts, 0.75).Count == 2, "freehand points are simplified");
var stroke = WhiteboardOps.FromPoints(WbKind.Stroke, new[] { new WbPoint(10, 10), new WbPoint(110, 60), new WbPoint(60, 210) });
Check(stroke.X == 10 && stroke.Width == 100 && stroke.Height == 200 && stroke.Points![1] == new WbPoint(1, 0.25), "stroke points normalized into the element box");
// style applies to the selection
wb.Select(new[] { r1.Id });
wb.FillColor = "#FFE08A"; wb.StrokeColor = "#E5484D"; wb.Thickness = 6;
Check(r1.Fill == "#FFE08A" && r1.Stroke == "#E5484D" && r1.StrokeWidth == 6 && r2.Fill is null, "stroke / fill / thickness applied to the selection only");
wb.Select(new[] { r2.Id });
Check(wb.FillColor is null && wb.IsStrokeAuto && wb.Thickness == 2, "style controls follow the selection");
// group (merge) and connectors
wb.Select(new[] { r1.Id, r2.Id });
wb.GroupCommand.Execute(null);
var group = wb.Data.Elements.Single(e => e.Kind == WbKind.Group);
Check(wb.Data.Elements.Count == 2 && group.Children!.Count == 2 && group.Width == 400 && wb.SelectedIds.Single() == group.Id, "merge: two elements become one group");
Check(wb.Data.Connectors.Count == 1 && wb.Data.Connectors[0].Links(group.Id, r3.Id), "merge: inner connector removed, outer one re-attached to the group");
group.X += 50; group.Width = 800; group.Rotation = 90;   // move + scale + rotate the group
wb.UngroupCommand.Execute(null);
Check(wb.Data.Elements.Count == 3 && wb.Data.Elements.All(e => e.Kind != WbKind.Group) && wb.SelectedIds.Count == 2, "ungroup restores the elements");
var u1 = wb.Data.Elements.First(e => e.Id == r1.Id);
Check(Math.Abs(u1.Width - 200) < 1e-6 && u1.Rotation == 90, "ungrouped elements keep the scale and rotation of the group");
// undo / redo
var countBefore = wb.Data.Elements.Count;
wb.UndoCommand.Execute(null);
Check(wb.Data.Elements.Any(e => e.Kind == WbKind.Group) && wb.CanRedo, "undo");
wb.RedoCommand.Execute(null);
Check(wb.Data.Elements.Count == countBefore && wb.Data.Elements.All(e => e.Kind != WbKind.Group), "redo");
// duplicate, copy/paste, delete
wb.Select(wb.Data.Elements.Select(e => e.Id).ToList());
var json2 = wb.CopySelection()!;
wb.DuplicateCommand.Execute(null);
Check(wb.Data.Elements.Count == 6 && wb.Data.Elements.Select(e => e.Id).Distinct().Count() == 6, "duplicate creates new ids");
Check(wb.Paste(json2, new WbPoint(1000, 1000)) && wb.Data.Elements.Count == 9, "paste copied elements");
wb.DeleteCommand.Execute(null);
Check(wb.Data.Elements.Count == 6, "delete selection");
wb.Select(new[] { r3.Id }); wb.DeleteCommand.Execute(null);
Check(wb.Data.Connectors.All(c => c.FromId != r3.Id && c.ToId != r3.Id), "deleting an element removes its connectors");
// persistence
wb.AddElement(stroke);
var saved = new WhiteboardService(entry.Folder).Load(wb.Data.Id)!;
Check(saved.Elements.Count == wb.Data.Elements.Count && saved.Elements.Last().Points!.Count == 3, "whiteboard auto-saved and reloaded");
var wbJson = File.ReadAllText(Directory.GetFiles(Path.Combine(entry.Folder, "whiteboards"), "board-*.json")[0]);
Check(wbJson.Contains("\"points\": \"0,0 1,0.25 0.5,1\"") && wbJson.Contains("\"kind\": \"Stroke\""), "points stored as a compact string");
// images
var svc = wb.Service;
var img = svc.SaveImage(new byte[] { 1, 2, 3 });
var orphan = svc.SaveImage(new byte[] { 4 });
wb.AddElement(new WbElement { Kind = WbKind.Image, Image = img, Width = 10, Height = 10 });
Check(svc.CleanupImages() == 1 && File.Exists(svc.ImagePath(img)) && !File.Exists(svc.ImagePath(orphan)), "unused image files are cleaned up, used ones kept");
// several boards
dialogs.PromptAnswer = "Level design";
dialogs.OnDialog = null;
wb.NewBoardCommand.Execute(null);
Check(wb.Boards.Count == 2 && wb.Data.Name == "Level design" && wb.Data.Elements.Count == 0, "second whiteboard");
dialogs.ConfirmAnswer = true;
wb.DeleteBoardCommand.Execute(null);
Check(wb.Boards.Count == 1 && wb.Data.Name == "Main" && wb.Data.Elements.Count > 0, "delete whiteboard, back to the first one");
board.IsWhiteboardMode = false;

// settings (theme)
var theme = new FakeTheme();
var mainT = new MainViewModel(accounts, projects, dialogs, theme, new SettingsService());
mainT.Start();
mainT.IsDarkMode = true;
var settings2 = new SettingsService(); settings2.Load();
Check(theme.IsDark && settings2.Current.Theme == "Dark", "dark mode applied and remembered");
mainT.IsDarkMode = false;

// project access
Console.WriteLine("== project access");
main.ShowProjects();
accounts.StartSession(anna);
dialogs.OnDialog = vm => { if (vm is ProjectAccessViewModel pa) { Check(pa.Members.Count == 1 && pa.SelectedMember!.Username == "Luca", "access dialog lists members"); pa.Password = "nope"; pa.LoginCommand.Execute(null); Check(pa.HasError, "wrong project password rejected"); pa.JoinCommand.Execute(null); return true; } return false; };
main.OpenProject(entry);
Check(main.Board != null && main.Board.Members.Count == 2, "non-member joined the project");
Check(main.Board!.CurrentUser.Username == "Anna Rossi", "tracking uses the logged-in account");
// comment by Anna on Luca's card: Anna can delete her own, Luca (creator) can delete it, Anna cannot delete Luca's
var lucaCard = main.Board.Columns.SelectMany(c => c.VisibleCards).First(c => c.Data.Comments.Count > 0);
dialogs.OnDialog = vm => { if (vm is CardEditorViewModel ed) { Check(!ed.Comments[0].CanDelete, "other user cannot delete someone else's comment"); ed.NewCommentText = "from anna"; ed.SaveCommand.Execute(null); return true; } return false; };
main.Board.EditCard(lucaCard);
Check(lucaCard.Data.Comments.Count == 2 && lucaCard.Data.Comments[1].AuthorName == "Anna Rossi", "typed comment saved with author");

// cloned project on another PC: login with project account imports it
var root2 = Path.Combine(Path.GetTempPath(), "hk-test-" + Guid.NewGuid().ToString("N"));
AppPaths.OverrideRoot(root2);
var accB = new AccountService(new XorProtector()); accB.Load();
var bob = accB.Create("Bob", "bobpass", null, "#30A46C"); accB.StartSession(bob);
var projB = new ProjectService();
var mainB = new MainViewModel(accB, projB, dialogs);
dialogs.OnDialog = vm => { if (vm is ProjectAccessViewModel pa) { pa.SelectedMember = pa.Members.First(m => m.Username == "Luca"); pa.Password = "secret1"; pa.LoginCommand.Execute(null); return !pa.HasError; } return false; };
mainB.OpenProject(projB.AddExisting(entry.Folder));
Check(mainB.Board != null && accB.Current!.Username == "Luca" && accB.Accounts.Count == 2, "login with a project account imports it on a new PC");
AppPaths.OverrideRoot(root);

// account rename is synced to the project
accounts.Update(anna, "Anna R", null, "#000000", null);
main.ShowProjects(); main.OpenProject(entry);
Check(main.Board!.Members.Any(m => m.Username == "Anna R" && m.AvatarColor == "#000000"), "profile changes synced to project accounts");

// ================================================================ v1.2
Console.WriteLine("== v1.2: profile picture, project icon, about");
main.ShowProjects();
Check(main.IsProjectList, "IsProjectList on the home screen");
accounts.StartSession(luca);
dialogs.OnDialog = null;
main.OpenProject(entry);
Check(!main.IsProjectList && main.Board != null, "IsProjectList false inside a project");
dialogs.ImageAnswer = "QUJDRA==";
dialogs.OnDialog = vm => { if (vm is AccountEditorViewModel ae) { ae.PickImageCommand.Execute(null); Check(ae.HasAvatarImage, "picture picked in the account dialog"); ae.SaveCommand.Execute(null); return ae.Result != null; } return false; };
main.EditProfileCommand.Execute(null);
Check(luca.AvatarImage == "QUJDRA==" && main.AccountImage == "QUJDRA==", "profile picture saved on the account");
Check(main.Board!.Members.First(m => m.Id == luca.Id).AvatarImage == "QUJDRA==", "profile picture synced to the project accounts");
Check(File.ReadAllText(Path.Combine(entry.Folder, "accounts.json")).Contains("\"avatarImage\""), "avatarImage written in the project accounts.json");
dialogs.OnDialog = vm => { if (vm is AccountEditorViewModel ae) { ae.RemoveImageCommand.Execute(null); ae.SaveCommand.Execute(null); return true; } return false; };
main.EditProfileCommand.Execute(null);
Check(luca.AvatarImage is null && !File.ReadAllText(Path.Combine(entry.Folder, "accounts.json")).Contains(luca.Id + "\",\n      \"username\": \"Luca\",\n      \"email\": \"luca@example.com\",\n      \"avatarColor\": \"#1F6F8B\",\n      \"avatarImage\""), "profile picture removed");

dialogs.ImageAnswer = "SUNPTg==";
dialogs.OnDialog = vm =>
{
    if (vm is ProjectSettingsViewModel ps)
    {
        ps.PickIconCommand.Execute(null);
        ps.FocusMinutes = 50; ps.ShortBreakMinutes = 10; ps.LongBreakEvery = 3; ps.TimerSound = "Bell"; ps.Volume = 40; ps.TimerHidden = false;
        ps.ImportMode = DocImportMode.Link; ps.OpenInPreview = false;
        ps.SaveCommand.Execute(null);
        return ps.Saved;
    }
    return false;
};
main.Board.OpenSettings();
Check(main.Board.HasProjectIcon && main.Board.ProjectIcon == "SUNPTg==", "project icon set from the settings");
Check(projects.TryLoadEntry(entry.Folder)!.Info.Icon == "SUNPTg==", "project icon persisted in project.json");
Check(projects.TryLoadEntry(entry.Folder)!.Info.Settings.Pomodoro is { FocusMinutes: 50, ShortBreakMinutes: 10, LongBreakEvery: 3, Sound: "Bell", Volume: 40 }, "timer settings persisted");
Check(projects.TryLoadEntry(entry.Folder)!.Info.Settings.Docs is { ImportMode: DocImportMode.Link, OpenInPreview: false }, "documentation settings persisted");
Check(main.Board.Pomodoro.TimeText == "50:00", "timer follows the new settings");
dialogs.OnDialog = vm => { if (vm is ProjectSettingsViewModel ps) { ps.FocusMinutes = 0; ps.SaveCommand.Execute(null); Check(ps.HasError && !ps.Saved, "invalid timer duration rejected"); } return false; };
main.Board.OpenSettings();
dialogs.Infos.Clear();
main.AboutCommand.Execute(null);
Check(dialogs.Infos.Count == 1 && dialogs.Infos[0].EndsWith("\n\n\nhighshore.studio@gmail.com") && !dialogs.Infos[0].Contains("Highshore Studio"), "about box ends with a blank line and the email");

Console.WriteLine("== v1.2: column colors per theme");
var col0 = main.Board.Columns[0];
dialogs.OnDialog = vm =>
{
    if (vm is ColumnStyleViewModel cs)
    {
        Check(cs.Light.IsCurrent && !cs.Dark.IsCurrent, "the dialog marks the theme in use");
        cs.Light.Background = "#E3F0F4"; cs.Light.TitleColor = "#16324F";
        cs.Dark.Background = "#17384A"; cs.Dark.TitleHex = "#6CC5E0";
        Check(cs.Dark.PreviewBackground == "#17384A" && cs.Light.PreviewBackground == "#E3F0F4", "previews use the chosen colors");
        cs.SaveCommand.Execute(null);
        return true;
    }
    return false;
};
col0.EditStyleCommand.Execute(null);
Check(col0.BackgroundColor == "#E3F0F4" && col0.TitleColor == "#16324F", "light colors used with the light theme");
main.IsDarkMode = true;
Check(col0.BackgroundColor == "#17384A" && col0.TitleColor == "#6CC5E0" && col0.HasCustomBackground, "dark colors used with the dark theme");
dialogs.OnDialog = vm => { if (vm is ColumnStyleViewModel cs) { Check(cs.Dark.IsCurrent, "dark marked in use"); cs.Dark.ResetBackgroundCommand.Execute(null); cs.Dark.ResetTitleCommand.Execute(null); cs.SaveCommand.Execute(null); return true; } return false; };
col0.EditStyleCommand.Execute(null);
Check(!col0.HasCustomBackground && !col0.HasCustomTitleColor, "dark colors reset: default in the dark theme");
main.IsDarkMode = false;
Check(col0.HasCustomBackground && col0.BackgroundColor == "#E3F0F4", "light colors are kept when the dark ones are reset");
var kanbanJson = File.ReadAllText(Path.Combine(entry.Folder, "kanban.json"));
Check(kanbanJson.Contains("\"background\": \"#E3F0F4\"") && !kanbanJson.Contains("backgroundDark") && kanbanJson.Contains("\"version\": 2"), "colors stored per theme (null values omitted)");
// boards written by v1.1 had one color for both themes
var oldBoard = new BoardData { Version = 1, Columns = { new ColumnData { Name = "Old", Background = "#FDECEC", TitleColor = "#B42318" } } };
ProjectService.Migrate(oldBoard);
Check(oldBoard.Version == 2 && oldBoard.Columns[0].BackgroundDark == "#FDECEC" && oldBoard.Columns[0].TitleColorDark == "#B42318", "v1.1 boards keep their column colors in both themes");
oldBoard.Columns[0].BackgroundDark = null;
ProjectService.Migrate(oldBoard);
Check(oldBoard.Columns[0].BackgroundDark is null, "the migration runs only once");

Console.WriteLine("== v1.2: default template");
dialogs.PromptAnswer = "Review";
main.Board.AddColumnCommand.Execute(null);
main.Board.MoveColumn(main.Board.Columns.Last(), -1);
var cardsBefore = main.Board.Data.Columns.Sum(c => c.Cards.Count);
Check(cardsBefore > 0, "the source project has cards");
dialogs.OnDialog = vm => { if (vm is ProjectSettingsViewModel ps) { Check(!ps.IsTemplate, "not a template yet"); ps.SetTemplateCommand.Execute(null); Check(ps.IsTemplate && ps.TemplateStatus.Contains("default template"), "settings: project set as default template"); } return false; };
main.Board.OpenSettings();
Check(File.Exists(Path.Combine(root, "template.json")), "template.json written in the app data folder");
var templateJson = File.ReadAllText(Path.Combine(root, "template.json"));
Check(!templateJson.Contains("Fix login bug") && !templateJson.Contains("\"cards\": [\n        {"), "the template contains no cards");
main.ShowProjects();
var listVm = (ProjectListViewModel)main.CurrentView!;
Check(listVm.HasTemplate && listVm.Projects.Single(p => p.Folder == entry.Folder).IsTemplate, "project list: template active and source project flagged");
Check(listVm.Projects.Single(p => p.Folder == entry.Folder).HasIcon, "project list item exposes the icon");
dialogs.OnDialog = null;
var fromTemplate = projects.Create("From Template", "", null, new ProjectEditorViewModel(projects, dialogs, luca).Color, luca);
var tBoard = projects.LoadBoard(fromTemplate.Folder);
Check(tBoard.Columns.Select(c => c.Name).SequenceEqual(projects.LoadBoard(entry.Folder).Columns.Select(c => c.Name)) && tBoard.Columns.Count == 4 && tBoard.Columns[2].Name == "Review", "new project: column names and order from the template");
Check(tBoard.Columns[0].Background == "#E3F0F4" && tBoard.Columns[0].TitleColor == "#16324F" && tBoard.Columns.Last().IsDone, "new project: column colors and done flag from the template");
Check(tBoard.Columns.All(c => c.Cards.Count == 0) && tBoard.Archived.Count == 0, "new project: no cards copied");
Check(tBoard.Tags.Select(t => t.Name).SequenceEqual(projects.LoadBoard(entry.Folder).Tags.Select(t => t.Name)), "new project: tags from the template");
Check(fromTemplate.Info.Settings.Pomodoro.FocusMinutes == 50 && fromTemplate.Info.Settings.Docs.ImportMode == DocImportMode.Link, "new project: timer and documentation settings from the template");
Check(fromTemplate.Info.Icon is null && !Directory.Exists(Path.Combine(fromTemplate.Folder, "whiteboards")) && !Directory.Exists(Path.Combine(fromTemplate.Folder, "docs")), "new project: no icon, whiteboards or documents copied");
Check(fromTemplate.Info.Color == entry.Info.Color, "new project dialog proposes the template color");
dialogs.ConfirmAnswer = true;
listVm.RestoreTemplateCommand.Execute(null);
Check(!listVm.HasTemplate && !File.Exists(Path.Combine(root, "template.json")), "Restore Original Template removes the template");
var plain = projects.Create("Plain", "", null, "#1F6F8B", luca);
Check(projects.LoadBoard(plain.Folder).Columns.Select(c => c.Name).SequenceEqual(new[] { "TODO", "In Progress", "Done" }) && plain.Info.Settings.Pomodoro.FocusMinutes == 25, "after restore: new projects use the original template");
projects.Delete(fromTemplate.Folder);
projects.Delete(plain.Folder);

Console.WriteLine("== v1.2: tomato timer");
var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
var sound = new FakeSound();
var pomSettings = new PomodoroSettings { FocusMinutes = 25, ShortBreakMinutes = 5, LongBreakMinutes = 15, LongBreakEvery = 2, AutoStartBreaks = true, AutoStartFocus = false, Sound = "Chime", Volume = 70 };
var pom = new PomodoroViewModel(pomSettings, sound, () => now);
string? lastMessage = null;
pom.Finished += m => lastMessage = m;
Check(pom.TimeText == "25:00" && !pom.IsRunning && pom.Phase == PomodoroPhase.Focus && pom.SessionText == "1/2", "timer starts stopped at the focus duration");
pom.ToggleCommand.Execute(null);
now = now.AddMinutes(10); pom.Tick();
Check(pom.IsRunning && pom.TimeText == "15:00" && Math.Abs(pom.Progress - 0.4) < 0.001, "countdown and progress");
pom.ToggleCommand.Execute(null);
now = now.AddMinutes(7); pom.Tick();
Check(!pom.IsRunning && pom.TimeText == "15:00", "pause freezes the countdown");
pom.Start();
now = now.AddMinutes(15).AddSeconds(1); pom.Tick();
Check(pom.Phase == PomodoroPhase.ShortBreak && pom.CompletedSessions == 1 && pom.IsRunning && pom.TimeText == "05:00", "focus finished: short break starts by itself");
Check(sound.Played.Count == 1 && sound.Played[0] == "Chime@70" && lastMessage!.Contains("short break"), "sound played and message raised at the end of a phase");
now = now.AddMinutes(5).AddSeconds(1); pom.Tick();
Check(pom.Phase == PomodoroPhase.Focus && !pom.IsRunning && pom.SessionText == "2/2", "break finished: focus waits for the user (auto-start off)");
pom.Start(); now = now.AddMinutes(26); pom.Tick();
Check(pom.Phase == PomodoroPhase.LongBreak && pom.TimeText == "15:00" && pom.CompletedSessions == 2, "long break every N sessions");
pom.SkipCommand.Execute(null);
Check(pom.Phase == PomodoroPhase.Focus && pom.CompletedSessions == 2 && pom.IsRunning, "skip moves to the next phase without counting it");
now = now.AddMinutes(3); pom.Tick();
pom.ResetCommand.Execute(null);
Check(!pom.IsRunning && pom.TimeText == "25:00", "reset");
pom.ApplySettings(new PomodoroSettings { FocusMinutes = 40, Hidden = true });
Check(pom.TimeText == "40:00" && !pom.IsVisible, "settings applied to an untouched timer; hidden flag");
pom.Dispose();
var wav = ToneSynth.Render("Chime", 80);
Check(wav != null && wav.Length > 20000 && wav[0] == 'R' && wav[8] == 'W' && BitConverter.ToInt32(wav, 40) == wav.Length - 44, "built-in sounds are valid WAV data");
Check(ToneSynth.Render("None", 80) is null && ToneSynth.Render("Bell", 0) is null && ToneSynth.Render("Digital", 100) != null, "None / volume 0 play nothing");
var half = ToneSynth.ScaleWav(ToneSynth.Render("Digital", 100)!, 50);
var full = ToneSynth.Render("Digital", 100)!;
var peakFull = Enumerable.Range(22, (full.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(full, i * 2)));
var peakHalf = Enumerable.Range(22, (half.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(half, i * 2)));
Check(Math.Abs(peakHalf - peakFull / 2) <= 2, "volume scales a custom wav file");

Console.WriteLine("== v1.2: markdown");
var sample = string.Join("\n", new[]
{
    "---", "tags: [design]", "---", "",
    "# Title with **bold**", "",
    "A paragraph with *italic*, **bold**, ***both***, ~~strike~~, `code` and a [link](https://example.com/a_b).",
    "Second line of the same paragraph with a [[Wiki Page|label]] and [[Other]].", "",
    "## List", "",
    "- one", "- two", "  - nested **x**", "  - nested y", "- three", "",
    "1. first", "2. second", "",
    "- [ ] todo", "- [x] done", "",
    "> quoted text", "> second line", "",
    "```csharp", "var x = 1; // **not bold**", "", "```", "",
    "| Name | Value |", "| :--- | ---: |", "| a | 1 |", "| b \\| c | `2` |", "",
    "![picture](.assets/img 1.png)", "",
    "---", "", "Setext heading", "==============", "", "snake_case_name and 2 * 3 and a\\*b"
});
var blocks = MdDoc.Parse(sample);
Check(blocks[0] is MdFrontMatter fm && fm.Raw == "tags: [design]", "front matter kept");
Check(blocks[1] is MdHeading { Level: 1 } h1 && h1.Inlines.Count == 2 && h1.Inlines[1].Bold && h1.Inlines[1].Text == "bold", "heading with bold");
var para = (MdParagraph)blocks[2];
Check(para.Inlines.Any(r => r.Italic && !r.Bold && r.Text == "italic") && para.Inlines.Any(r => r.Bold && !r.Italic && r.Text == "bold") &&
      para.Inlines.Any(r => r.Bold && r.Italic && r.Text == "both") && para.Inlines.Any(r => r.Strike && r.Text == "strike") &&
      para.Inlines.Any(r => r.Code && r.Text == "code"), "inline styles");
Check(para.Inlines.Any(r => r.Href == "https://example.com/a_b" && r.Text == "link" && !r.IsWiki), "markdown link");
Check(para.Inlines.Any(r => r.Kind == MdInlineKind.LineBreak), "a new line inside a paragraph is a line break");
Check(para.Inlines.Any(r => r.IsWiki && r.Href == "Wiki Page" && r.Text == "label") && para.Inlines.Any(r => r.IsWiki && r.Href == "Other" && r.Text == "Other"), "wiki links with and without label");
var ul = blocks.OfType<MdList>().First();
Check(!ul.Ordered && ul.Items.Count == 3 && ul.Items[1].Blocks.Count == 2 && ul.Items[1].Blocks[1] is MdList { Items.Count: 2 }, "nested list");
var ol = blocks.OfType<MdList>().Skip(1).First();
Check(ol.Ordered && ol.Items.Count == 2, "ordered list");
var tasks = blocks.OfType<MdList>().Skip(2).First();
Check(tasks.Items[0].Checked == false && tasks.Items[1].Checked == true, "task list");
Check(blocks.OfType<MdQuote>().Single().Blocks.Single() is MdParagraph qp && MdDoc.PlainText(qp.Inlines) == "quoted text second line", "quote");
Check(blocks.OfType<MdCodeBlock>().Single() is { Language: "csharp" } cb && cb.Code == "var x = 1; // **not bold**\n", "code block keeps its text");
var table = blocks.OfType<MdTable>().Single();
Check(table.Header.Count == 2 && table.Rows.Count == 2 && table.Align[0] == MdAlign.Left && table.Align[1] == MdAlign.Right &&
      MdDoc.PlainText(table.Rows[1][0]) == "b | c" && table.Rows[1][1][0].Code, "table with alignment, escaped pipe and code");
Check(blocks.OfType<MdParagraph>().Any(p => p.Inlines.Count == 1 && p.Inlines[0].Kind == MdInlineKind.Image && p.Inlines[0].Source == ".assets/img 1.png" && p.Inlines[0].Text == "picture"), "image");
Check(blocks.OfType<MdRule>().Count() == 1 && blocks.OfType<MdHeading>().Any(h => h.Level == 1 && MdDoc.PlainText(h.Inlines) == "Setext heading"), "rule and setext heading");
Check(MdDoc.PlainText(((MdParagraph)blocks.Last()).Inlines) == "snake_case_name and 2 * 3 and a*b", "underscores inside words and escaped asterisks are plain text");
var written = MdDoc.Write(blocks);
Check(Dump(MdDoc.Parse(written)) == Dump(blocks), "write + parse gives the same document");
Check(MdDoc.Write(MdDoc.Parse(written)) == written, "writing is stable (second pass identical)");
Check(written.Contains("**a\\_b**") == false && written.Contains("snake_case_name") && written.Contains("a\\*b"), "only the needed characters are escaped");
Check(MdDoc.Outline(sample).Select(o => $"{o.Level}:{o.Text}@{o.Line}").SequenceEqual(new[] { "1:Title with bold@4", "2:List@9", "1:Setext heading@40" }), "outline with line numbers");
Check(MdDoc.CountWords("Hello brave new-world, it's 2026!") == 6, "word count");
var html = MdDoc.ToHtml(blocks, "Doc <1>");
Check(html.Contains("<title>Doc &lt;1&gt;</title>") && html.Contains("<h1>Title with <strong>bold</strong></h1>") && html.Contains("<del>strike</del>") &&
      html.Contains("<input type=\"checkbox\" disabled checked>") && html.Contains("<th style=\"text-align:left\">Name</th>") && html.Contains("<pre><code>var x = 1;"), "HTML export");
// formatting written from the editor model
var styled = new MdParagraph { Inlines = { new MdInline { Text = "bold ", Bold = true }, new MdInline { Text = "and" }, new MdInline { Text = " italic", Italic = true },
    new MdInline { Text = " mix", Bold = true, Italic = true }, new MdInline { Text = "ed", Bold = true } } };
var styledText = MdDoc.Write(new MdBlock[] { styled });
Check(styledText == "**bold** and _italic_ **_mix_ed**\n" || styledText == "**bold** and _italic_ **_mix_**ed\n" || Dump(MdDoc.Parse(styledText)) == Dump(new MdBlock[] { styled }), "spaces move outside the markers: " + styledText.Trim());
Check(Dump(MdDoc.Parse(styledText)) == Dump(new MdBlock[] { styled }), "styled runs survive the round trip");
var indented = MdDoc.Parse("Text\n\n    int a = 1;\n\n    int b = 2;\n\nMore\n\tnot code (continues the paragraph)");
Check(indented.Count == 3 && indented[1] is MdCodeBlock { Code: "int a = 1;\n\nint b = 2;" } && indented[2] is MdParagraph, "code blocks written with 4 spaces are kept as code");
// web addresses typed as plain text must survive (underscores and asterisks are not escaped inside them)
var urlText = new MdBlock[] { new MdParagraph { Inlines = { MdInline.Plain("see https://github.com/u/r/src/__init__.py, and a_b *c*") } } };
var urlWritten = MdDoc.Write(urlText);
var urlBack = (MdParagraph)MdDoc.Parse(urlWritten)[0];
Check(urlWritten.Contains("https://github.com/u/r/src/__init__.py,") && urlBack.Inlines.Any(r => r.Href == "https://github.com/u/r/src/__init__.py" && r.Text == r.Href) &&
      MdDoc.Write(MdDoc.Parse(urlWritten)) == MdDoc.Write(MdDoc.Parse(MdDoc.Write(MdDoc.Parse(urlWritten)))) &&
      MdDoc.PlainText(MdDoc.Parse(MdDoc.Write(MdDoc.Parse(urlWritten)))).Trim() == "see https://github.com/u/r/src/__init__.py, and a_b *c*", "web addresses in plain text are kept intact: " + urlWritten.Trim());
var titled = MdDoc.Parse("[site](https://example.com \"The title\") and ![pic](a.png 'Photo')");
var titledRuns = ((MdParagraph)titled[0]).Inlines;
Check(titledRuns[0].Title == "The title" && titledRuns.Last().Title == "Photo" && MdDoc.Write(titled) == "[site](https://example.com \"The title\") and ![pic](a.png \"Photo\")\n", "link and picture titles are kept");
Check(MdDoc.Write(MdDoc.Parse("see https://github.com/u/r/src/__init__.py typed plainly, (https://a.example.com/x) and https://b.example.com/y.")) ==
      "see https://github.com/u/r/src/__init__.py typed plainly, (https://a.example.com/x) and https://b.example.com/y.\n" &&
      MdDoc.Write(MdDoc.Parse("<https://example.com/page>, then text")) == "https://example.com/page, then text\n" &&
      MdDoc.Write(MdDoc.Parse("x<https://example.com/page>y")) == "x<https://example.com/page>y\n" &&
      MdDoc.Write(MdDoc.Parse("https://example.com/alone")) == "https://example.com/alone\n",
      "a web address typed plainly is written back exactly as it was");
Check(((MdCodeBlock)MdDoc.Parse("```make\nall:\n\tgcc main.c\n```")[0]).Code == "all:\n\tgcc main.c", "tabs inside a code block are kept");
Check(MdDoc.UnsupportedSyntax("# T\n\nplain *text* with List<string> and `<div>` in code\n\n```\n<div>\n```\n") is null &&
      MdDoc.UnsupportedSyntax("text\n\n<div align=\"center\">x</div>") == "HTML" &&
      MdDoc.UnsupportedSyntax("see [docs][1]\n\n[1]: https://example.com") == "reference-style links" &&
      MdDoc.UnsupportedSyntax("note[^1]\n\n[^1]: the note") == "footnotes" &&
      MdDoc.UnsupportedSyntax("a <!-- hidden --> b") == "HTML comments" && MdDoc.UnsupportedSyntax("line<br>break and <https://example.com>") is null,
      "documents with HTML, reference links or footnotes are detected (the Preview stays read-only for them)");
foreach (var nasty in new[] { "[a](<)", "[", "![", "[[", "**", "`", "|", "| a |\n|-", "- ", "> ", "```", "[a](", "*_~`[]()<>!#", "\\", "[x]: ", "1.", "<", "[a]\n(<" })
{
    try { MdDoc.Write(MdDoc.Parse(nasty)); DocsService.ExtractLinks(nasty); MdDoc.Outline(nasty); MdDoc.UnsupportedSyntax(nasty); }
    catch (Exception ex) { Check(false, $"no crash on '{nasty.Replace("\n", "\\n")}': {ex.GetType().Name}"); }
}
Check(true, "odd fragments of markdown never crash the parser or the link scanner");
var twoLists = new MdBlock[]
{
    new MdList { Items = { new MdListItem { Blocks = { new MdParagraph { Inlines = { MdInline.Plain("a") } } } } } },
    new MdList { Items = { new MdListItem { Checked = false, Blocks = { new MdParagraph { Inlines = { MdInline.Plain("task") } } } } } },
    new MdList { Items = { new MdListItem { Blocks = { new MdParagraph { Inlines = { MdInline.Plain("c") } } } } } }
};
Check(MdDoc.Write(twoLists) == "- a\n\n* [ ] task\n\n- c\n" && Dump(MdDoc.Parse(MdDoc.Write(twoLists))) == Dump(twoLists), "lists in a row stay separate (alternating markers)");
Check(MdDoc.Write(new MdBlock[] { new MdParagraph { Inlines = { MdInline.Plain("# not a heading") } }, new MdParagraph { Inlines = { MdInline.Plain("1. not a list") } }, new MdParagraph { Inlines = { MdInline.Plain("- neither") } } })
      == "\\# not a heading\n\n1\\. not a list\n\n\\- neither\n", "text that looks like markdown is escaped");

// random documents: whatever the editor can produce must come back identical
var rng = new Random(20261002);
var bad = 0; string? firstBad = null;
for (var iter = 0; iter < 1500; iter++)
{
    var doc = RandomBlocks(rng, 0);
    var text = MdDoc.Write(doc);
    var back = MdDoc.Parse(text);
    if (Dump(back) != Dump(doc))
    {
        bad++;
        if (bad <= 6) Console.WriteLine($"--- markdown ---\n{text}--- expected ---\n{Dump(doc)}--- got ---\n{Dump(back)}");
    }
}
if (firstBad != null) Console.WriteLine(firstBad);
Check(bad == 0, $"1500 random documents survive write + parse ({bad} failed)");

Console.WriteLine("== v1.2: documentation files");
var docsEntry = projects.Create("Docs Project", "", null, "#1F6F8B", luca);
var docs = new DocsService(docsEntry.Folder);
Check(docs.Tree().Children.Count == 0 && !Directory.Exists(docs.Root), "no docs folder until something is created");
var readme = docs.CreateDocument("", "README", "# Readme\n\nSee [[Combat]] and [[Missing Page]].\n");
var design = docs.CreateFolder("", "Design");
var combat = docs.CreateDocument(design, "Combat", "# Combat\n\nBack to [the readme](../README.md) and to [[Enemies#Bosses|the bosses]].\n\n`[[Not A Link]]`\n\n```\n[[Also Not]]\n```\n");
var enemies = docs.CreateDocument(design, "Enemies.md", "# Enemies\n\n## Bosses\n\nSee [[Combat]].\n");
Check(readme == "README.md" && combat == "Design/Combat.md" && File.Exists(Path.Combine(docsEntry.Folder, "docs", "Design", "Combat.md")), "documents are plain files in docs/, folders are folders");
Check(docs.CreateDocument(design, "Combat") == "Design/Combat 2.md", "duplicate names get a number");
docs.Delete("Design/Combat 2.md");
Check(!File.Exists(docs.FullPath("Design/Combat 2.md")) && Directory.GetFiles(Path.Combine(docs.Root, ".trash")).Length == 1, "deleted documents go to docs/.trash");
File.WriteAllText(Path.Combine(docs.Root, ".hidden.md"), "x");
var tree = docs.Tree();
Check(tree.Children.Select(c => c.Name).SequenceEqual(new[] { "Design", "README.md" }) && tree.Children[0].Children.Select(c => c.Name).SequenceEqual(new[] { "Combat.md", "Enemies.md" }), "tree mirrors the folder (folders first, hidden items skipped)");
try { docs.CreateDocument("", "bad:name"); Check(false, "invalid names rejected"); } catch (InvalidOperationException) { Check(true, "invalid names rejected"); }
try { docs.FullPath("../outside.md"); Check(false, "paths cannot leave the docs folder"); } catch (InvalidOperationException) { Check(true, "paths cannot leave the docs folder"); }
var links = DocsService.ExtractLinks(docs.Read(combat));
Check(links.Count == 2 && !links[0].IsWiki && links[0].Target == "../README.md" && links[1].IsWiki && links[1].Target == "Enemies" && links[1].Anchor == "Bosses", "links extracted (code is ignored)");
var docIndex = docs.Index();
Check(docIndex.Resolve(combat, "../README.md", false)?.Path == readme && docIndex.Resolve(readme, "Combat", true)?.Path == combat &&
      docIndex.Resolve(readme, "design/enemies", true)?.Path == enemies && docIndex.Resolve(readme, "Missing Page", true) is null &&
      docIndex.Resolve(readme, "https://example.com", false) is null, "links resolved by path and by name");
Check(docs.Backlinks(combat).Select(d => d.Path).OrderBy(p => p).SequenceEqual(new[] { "Design/Enemies.md", "README.md" }), "backlinks");
var graph = docs.BuildGraph();
Check(graph.Nodes.Count == 4 && graph.Nodes.Count(nd => nd.IsGhost) == 1 && graph.Edges.Count == 3 &&
      graph.Nodes.Single(nd => nd.Id == combat).Degree == 2 && graph.Nodes.Single(nd => nd.Id == combat).Group == "Design", "graph: documents, references and a ghost node for the missing page");
var hits = docs.Search("bosses");
Check(hits.Count == 2 && hits.All(hh => hh.Line >= 0) && docs.Search("combat").Any(hh => hh.Line == -1 && hh.Path == combat) && docs.Search("zzz").Count == 0, "search in names and text");
// rename: links in the other documents follow
var renamed = docs.Rename(combat, "Fighting");
Check(renamed == "Design/Fighting.md" && docs.Read(readme).Contains("[[Fighting]]") && docs.Read(enemies).Contains("See [[Fighting]]."), "rename updates wiki links");
var movedTo = docs.Move(renamed, "");
Check(movedTo == "Fighting.md" && docs.Read(movedTo).Contains("[the readme](README.md)") && docs.Read(movedTo).Contains("[[Enemies#Bosses|the bosses]]"), "move rebases the relative links of the moved document");
docs.Write(readme, docs.Read(readme) + "\n[direct](Fighting.md#Top)\n");
var sub = docs.CreateFolder("", "Archive");
docs.Move(movedTo, sub);
Check(docs.Read(readme).Contains("[direct](Archive/Fighting.md#Top)") && docs.Read("Archive/Fighting.md").Contains("[the readme](../README.md)"), "move updates markdown links in both directions (anchor kept)");
var folderRenamed = docs.Rename("Design", "Game Design");
Check(folderRenamed == "Game Design" && docs.Find("Game Design/Enemies.md") != null && docs.Index().Resolve("Archive/Fighting.md", "Enemies", true)?.Path == "Game Design/Enemies.md", "folder rename keeps wiki links valid");
try { docs.Move("Game Design", "Game Design/Sub"); Check(false, "a folder cannot be moved into itself"); } catch (InvalidOperationException) { Check(true, "a folder cannot be moved into itself"); }
// import: copy or link
var external = Path.Combine(root, "external");
Directory.CreateDirectory(external);
var extFile = Path.Combine(external, "Spec.md");
File.WriteAllText(extFile, "# Spec\r\n\r\nOriginal text. [[README]]\r\n");
var copied = docs.Import(extFile, "", link: false);
Check(copied == "Spec.md" && File.Exists(docs.FullPath(copied)) && docs.Find(copied) is { IsLink: false }, "import as copy");
var linked = docs.Import(extFile, "Archive", link: true);
var linkNode = docs.Find(linked)!;
Check(linked == "Archive/Spec.md.link" && linkNode.IsLink && linkNode.Name == "Spec.md" && linkNode.Kind == DocKind.Text && linkNode.LinkTarget == extFile && !linkNode.IsBroken, "import as link creates a pointer file");
Check(docs.Read(linked).Contains("Original text.") && !File.Exists(docs.FullPath("Archive/Spec.md")), "a linked document is read from the original file");
docs.Write(linked, "# Spec\r\n\r\nEdited.\r\n");
Check(File.ReadAllText(extFile).Contains("Edited."), "editing a linked document changes the original");
var pic = Path.Combine(external, "shot.png");
File.WriteAllBytes(pic, new byte[] { 1, 2, 3 });
Check(docs.Find(docs.Import(pic, "", false))!.Kind == DocKind.Image && docs.Find(docs.Import(Path.Combine(external), "", false))!.IsFolder, "pictures and whole folders can be imported");
var asset = docs.SaveAsset(new byte[] { 9, 9 });
Check(asset.StartsWith(".assets/image-") && docs.ResolveImage("Game Design/Enemies.md", "../" + asset) == docs.FullPath(asset) && docs.Tree().Children.All(c => c.Name != ".assets"), "pasted pictures are stored in docs/.assets (hidden from the tree)");
File.Delete(extFile);
Check(docs.Find(linked)!.IsBroken, "a link whose original was deleted is flagged as broken");
docs.Delete(linked);
Check(docs.Find(linked) is null, "removing a link");
// encodings: a file is written back the way it was found
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var ansiFile = docs.FullPath("Legacy.txt");
File.WriteAllBytes(ansiFile, System.Text.Encoding.GetEncoding(1252).GetBytes("perché così\r\n"));
var ansiText = docs.Read("Legacy.txt", out var ansiEncoding);
Check(ansiText == "perché così\r\n" && ansiEncoding.IsSingleByte, "old Windows (ANSI) text files are read correctly");
docs.Write("Legacy.txt", ansiText + "già\r\n", ansiEncoding);
Check(File.ReadAllBytes(ansiFile).SequenceEqual(System.Text.Encoding.GetEncoding(1252).GetBytes("perché così\r\ngià\r\n")), "and written back in the same encoding");
var upgraded = docs.Write("Legacy.txt", "freccia → qui\n", ansiEncoding);
Check(upgraded is System.Text.UTF8Encoding && docs.Read("Legacy.txt") == "freccia → qui\n", "a character the old encoding cannot store upgrades the file to UTF-8 instead of becoming '?'");
File.WriteAllBytes(docs.FullPath("Bom.md"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("# Bom\n")).ToArray());
var bomText = docs.Read("Bom.md", out var bomEncoding);
docs.Write("Bom.md", bomText + "x\n", bomEncoding);
Check(bomText == "# Bom\n" && File.ReadAllBytes(docs.FullPath("Bom.md")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "a UTF-8 byte-order mark is kept");
File.WriteAllBytes(docs.FullPath("Wide.md"), System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("# Wide é\n")).ToArray());
Check(docs.Read("Wide.md") == "# Wide é\n", "UTF-16 files are read");
foreach (var f in new[] { "Legacy.txt", "Bom.md", "Wide.md" }) File.Delete(docs.FullPath(f));
// more link cases
docs.Write("Game Design/Enemies.md", docs.Read("Game Design/Enemies.md") + "\n| Doc | Note |\n| --- | --- |\n| [[Fighting\\|the fights]] | x |\n");
var again = docs.Rename("Archive/Fighting.md", "Battles");
Check(docs.Read("Game Design/Enemies.md").Contains("| [[Battles\\|the fights]] | x |"), "rename keeps the escaped separator of a wiki link inside a table");
docs.Rename(again, "Fighting");
try { docs.Import(Path.GetDirectoryName(docs.Root)!, "", false); Check(false, "a folder containing the docs cannot be imported into them"); }
catch (InvalidOperationException) { Check(true, "a folder containing the docs cannot be imported into them"); }
Check(docs.Import(docs.FullPath("Game Design"), "", false) == "Game Design", "a folder that is already in the documentation is not copied again");
Check(docs.ResolveImage("Game Design/Enemies.md", "/" + asset) == docs.FullPath(asset), "a picture path starting with / is relative to the docs folder");
var broken = (byte[])ToneSynth.Render("Digital", 100)!.Clone(); BitConverter.GetBytes(-8).CopyTo(broken, 16);
Check(ToneSynth.ScaleWav(broken, 50) == broken, "a damaged wav file is returned as it is");
Check(DocTemplate.All.Count >= 8 && DocTemplate.All[0].Render("My Doc") == "# My Doc\n\n" && DocTemplate.All.All(t => MdDoc.Parse(t.Render("X")).Count > 0), "document templates");

Console.WriteLine("== v1.2: documentation workspace");
dialogs.OnDialog = null;
dialogs.Infos.Clear();
var dvm = new DocsViewModel(docsEntry.Folder, dialogs, () => new DocsSettings { ImportMode = DocImportMode.Ask, OpenInPreview = true });
dvm.WorkspaceWidth = 1200; dvm.WorkspaceHeight = 700;
Check(dvm.Nodes.Count >= 3 && dvm.Nodes[0].IsFolder && dvm.IsWorkspaceEmpty, "tree loaded");
Check(dvm.Nodes.First(n => n.Path == "README.md").Name == "README" && dvm.IsTreeVisible, "the tree shows markdown documents without the .md extension");
dvm.ToggleTreeCommand.Execute(null);
Check(!dvm.IsTreeVisible, "the tree can be hidden to give the document the whole area");
dvm.ToggleTreeCommand.Execute(null);
var openDoc = dvm.Open("README.md")!;
Check(dvm.Tabs.Count == 1 && dvm.ActiveTab == openDoc && openDoc.IsPreview && openDoc.Text.StartsWith("# Readme") && dvm.SelectedNode?.Path == "README.md", "document opened in a tab (formatted view by default)");
Check(dvm.Open("README.md") == openDoc && dvm.Tabs.Count == 1, "opening twice reuses the tab");
openDoc.Text += "\nNew line.\n";
Check(openDoc.IsDirty && openDoc.Header.EndsWith("•"), "editing marks the document as modified");
dvm.AutoSave(idleSeconds: -1);
Check(!openDoc.IsDirty && docs.Read("README.md").Contains("New line."), "autosave writes the file");
var winDoc = dvm.Open("Game Design/Enemies.md", inWindow: true)!;
var win = dvm.Windows.Single();
Check(dvm.Tabs.Count == 1 && win.Document == winDoc && Math.Abs(win.Width - (1200 / 2 - 16)) < 1 && win.X > 500 && win.Height > 600, "document opened in a floating window on half of the workspace");
win.X = 40; win.Y = 30; win.Width = 100;
Check(win.Width == 280 && win.X == 40, "floating windows can be moved and resized (with a minimum size)");
winDoc.DockCommand.Execute(null);
Check(dvm.Windows.Count == 0 && dvm.Tabs.Count == 2 && dvm.ActiveTab == winDoc, "a window can be enlarged into a tab");
winDoc.FloatCommand.Execute(null);
Check(dvm.Windows.Count == 1 && dvm.Tabs.Count == 1 && dvm.ActiveTab == openDoc, "a tab can be detached into a window");
// crlf files keep their line endings
File.WriteAllText(docs.FullPath("Spec.md"), "# Spec\r\n\r\nLine\r\n");
var crlf = dvm.Open("Spec.md")!;
Check(!crlf.Text.Contains('\r'), "the editor works with \\n");
crlf.Text += "More\n"; crlf.Save();
Check(File.ReadAllText(docs.FullPath("Spec.md")) == "# Spec\r\n\r\nLine\r\nMore\r\n", "Windows line endings are preserved on save");
File.WriteAllText(docs.FullPath("Spec.md"), "# Changed outside\n"); File.SetLastWriteTimeUtc(docs.FullPath("Spec.md"), DateTime.UtcNow.AddMinutes(1));
dvm.CheckExternalChanges();
Check(crlf.Text == "# Changed outside\n" && !crlf.IsDirty, "a file changed by another program is reloaded");
dvm.Close(crlf);
// a document that cannot be saved is not closed silently
var locked = dvm.Open("Spec.md")!;
locked.Text += "unsaved\n";
var specFile = docs.FullPath("Spec.md");
File.Delete(specFile); Directory.CreateDirectory(specFile); // a folder in its place: the save must fail
dialogs.ConfirmAnswer = false;
dvm.Close(locked);
Check(dvm.Tabs.Contains(locked) && locked.IsDirty, "a document that cannot be saved stays open (the user is asked)");
Directory.Delete(specFile);
dvm.Close(locked);
Check(!dvm.Tabs.Contains(locked) && File.ReadAllText(specFile).EndsWith("unsaved\n"), "and it is saved and closed once the file is writable again");
dialogs.ConfirmAnswer = true;
var htmlDoc = docs.CreateDocument("", "Html Doc", "# Html\n\n<div align=\"center\">centered</div>\n");
var htmlVm = dvm.Open(htmlDoc)!;
Check(htmlVm.IsPreviewLocked && htmlVm.IsMarkdown && htmlVm.PreviewLockText.Contains("HTML"), "a document with HTML opens in the Markdown view, its Preview is read-only");
htmlVm.Text = "# Html\n\nplain now\n"; htmlVm.IsPreview = true;
Check(!htmlVm.IsPreviewLocked && htmlVm.IsPreview, "the Preview is editable again once the HTML is gone");
dvm.Close(htmlVm); docs.Delete(htmlDoc);
// links
dialogs.ConfirmAnswer = true;
dvm.FollowLink(openDoc, "Missing Page", isWiki: true);
Check(docs.Find("Missing Page.md") != null && dvm.ActiveTab?.Path == "Missing Page.md", "following a wiki link to a missing document creates it");
dvm.FollowLink(openDoc, "Enemies#Bosses", isWiki: true);
Check(dvm.Windows.Single().Document.Path == "Game Design/Enemies.md" && dvm.Windows.Single().Document.PendingScroll?.Text == "Bosses", "a link with #heading opens the document at that heading");
dvm.FollowLink(openDoc, "https://example.com", false);
Check(dialogs.Opened.Last() == "https://example.com", "web links open in the browser");
Check(dvm.WikiTarget(docs.Find("Game Design/Enemies.md")!) == "Enemies", "wiki target of a document");
// rename through the view model keeps open documents attached
dialogs.PromptAnswer = "Monsters";
dvm.RenameCommand.Execute(dvm.FindNode("Game Design/Enemies.md"));
Check(dvm.Windows.Single().Document.Path == "Game Design/Monsters.md" && dvm.FindNode("Game Design/Monsters.md") != null && docs.Read("Archive/Fighting.md").Contains("[[Monsters#Bosses|the bosses]]"), "rename from the tree: open documents follow, links updated");
dvm.MoveNode(dvm.FindNode("Game Design/Monsters.md")!, "Archive");
Check(dvm.Windows.Single().Document.Path == "Archive/Monsters.md", "drag & drop move in the tree");
// new document from a template
dialogs.OnDialog = vm => { if (vm is NewDocumentViewModel nd) { nd.Name = "Sprint 1"; nd.Template = nd.Templates.First(t => t.Name == "Meeting notes"); nd.CreateCommand.Execute(null); return !nd.HasError; } return false; };
dvm.SelectedNode = dvm.FindNode("Archive");
var created = dvm.NewDocument();
Check(created?.Path == "Archive/Sprint 1.md" && created.Text.StartsWith("# Sprint 1") && created.Text.Contains("## Agenda"), "new document from a template, inside the selected folder");
dialogs.OnDialog = null;
// import with "ask": yes = copy, no = link
var ext2 = Path.Combine(external, "Notes.txt"); File.WriteAllText(ext2, "plain notes");
dialogs.AskAnswer = false;
dvm.Import(new[] { ext2 }, "");
Check(docs.Find("Notes.txt.link") is { IsLink: true }, "import (ask -> No) creates a link");
dialogs.AskAnswer = true;
dvm.Import(new[] { ext2 }, "Archive");
Check(docs.Find("Archive/Notes.txt") is { IsLink: false }, "import (ask -> Yes) copies the file");
dialogs.AskAnswer = null;
var filesBefore = docs.Files().Count;
dvm.Import(new[] { ext2 }, "Game Design");
Check(docs.Files().Count == filesBefore, "import (ask -> Cancel) does nothing");
// search, info panel, graph
dvm.SearchText = "bosses";
Check(dvm.IsSearching && dvm.SearchResults.Count >= 2 && dvm.SearchSummary.Contains("result"), "search results replace the tree");
dvm.SearchText = "";
dvm.OpenAsTab(dvm.Windows.Single().Document);
dvm.IsInfoVisible = true;
Check(dvm.InfoDocument!.Outline.Select(o => o.Text).SequenceEqual(new[] { "Enemies", "Bosses" }) && dvm.InfoDocument.Backlinks.Any(b => b.Path == "Archive/Fighting.md") && dvm.InfoDocument.StatsText.Contains("words"), "info panel: outline, backlinks, statistics");
dvm.IsGraphVisible = true;
Check(dvm.Graph.Nodes.Count >= 5 && dvm.Graph.Edges.Count >= 3 && dvm.GraphSummary.Contains("links") && !dvm.IsEditorVisible, "graph of the documentation built");
dvm.SelectGraphNode("Archive/Fighting.md");
Check(dvm.IsGraphDocument && dvm.GraphTitle == "Fighting" && dvm.GraphMarkdown.StartsWith("# Combat"), "clicking a node shows the preview of the document");
dvm.GraphOpenInWindowCommand.Execute(null);
Check(dvm.Windows.Any(w => w.Document.Path == "Archive/Fighting.md"), "the previewed document can be opened in a window");
dvm.OpenCommand.Execute(dvm.FindNode("README.md"));
Check(!dvm.IsGraphVisible && dvm.ActiveTab?.Path == "README.md", "opening a document leaves the graph");
// export + delete
dialogs.SaveFileAnswer = Path.Combine(root, "export.html");
dvm.ExportHtml(dvm.ActiveTab!);
Check(File.Exists(dialogs.SaveFileAnswer) && File.ReadAllText(dialogs.SaveFileAnswer).Contains("<h1>Readme</h1>"), "export to HTML");
dvm.DeleteCommand.Execute(dvm.FindNode("Archive"));
Check(docs.Find("Archive") is null && dvm.OpenDocuments.All(d => !d.Path.StartsWith("Archive/")), "deleting a folder closes its open documents");
dvm.Shutdown();
// through the project screen
main.OpenProject(docsEntry);
main.ShowDocsCommand.Execute(null);
Check(main.Board!.IsDocsMode && !main.Board.IsKanbanMode && !main.Board.IsWhiteboardMode && main.Board.Docs != null, "Documentation mode in the project screen");
var live = main.Board.Docs!.Open("README.md")!;
live.Text += "typed and not saved\n";
main.ShowKanbanCommand.Execute(null);
Check(main.Board.IsKanbanMode && docs.Read("README.md").Contains("typed and not saved"), "leaving the documentation saves the open documents");
main.Board.Mode = ProjectMode.Docs;
live.Text += "typed before closing\n";
main.ShowProjects();
Check(docs.Read("README.md").Contains("typed before closing"), "closing the project saves the open documents");

Console.WriteLine("== v1.2: graph layout");
var gm = new GraphModel();
foreach (var id in new[] { "a", "b", "c", "d", "e", "lonely" }) gm.Add(id, id.ToUpperInvariant());
gm.Connect("a", "b"); gm.Connect("a", "c"); gm.Connect("a", "d"); gm.Connect("d", "e");
Check(!gm.Connect("b", "a") && !gm.Connect("a", "a") && gm.Edges.Count == 4 && gm.Find("a")!.Degree == 3, "no duplicate or self edges; degree counted");
gm.Seed();
gm.Run(2000);
double Dist(string x, string y) { var p = gm.Find(x)!; var q = gm.Find(y)!; return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)); }
Check(gm.IsSettled && gm.Nodes.All(nd => double.IsFinite(nd.X) && double.IsFinite(nd.Y)), "layout settles on finite positions");
Check(Dist("a", "b") < Dist("b", "e") && Dist("d", "e") < Dist("lonely", "e") && gm.Nodes.SelectMany(p => gm.Nodes.Where(q => q != p).Select(q => Dist(p.Id, q.Id))).Min() > 20, "linked nodes end up close, the others apart, none overlapping");
var hub = gm.Find("a")!;
Check(gm.HitTest(hub.X + 2, hub.Y - 2) == hub && gm.HitTest(hub.X + 500, hub.Y) is null && hub.Radius > gm.Find("lonely")!.Radius, "hit test; hubs are bigger");
var gm2 = new GraphModel();
foreach (var id in new[] { "a", "b", "c", "d", "e", "lonely" }) gm2.Add(id, id);
gm2.Connect("a", "b"); gm2.Connect("a", "c"); gm2.Connect("a", "d"); gm2.Connect("d", "e");
gm2.Seed(); gm2.Run(2000);
Check(gm.Nodes.Zip(gm2.Nodes).All(z => Math.Abs(z.First.X - z.Second.X) < 1e-9), "the layout is deterministic");
var wbData = new WhiteboardData();
var e1 = new WbElement { Kind = WbKind.Text, Text = "Player\nstats" }; var e2 = new WbElement { Kind = WbKind.Rectangle }; var e3 = new WbElement { Kind = WbKind.Ellipse }; var e4 = new WbElement { Kind = WbKind.Rectangle };
wbData.Elements.AddRange(new[] { e1, e2, e3, e4 });
WhiteboardOps.AddConnector(wbData, e1.Id, e2.Id); WhiteboardOps.AddConnector(wbData, e2.Id, e4.Id);
var wg = WhiteboardGraph.Build(wbData);
Check(wg.Nodes.Count == 3 && wg.Edges.Count == 2 && wg.Find(e1.Id)!.Label == "Player" && wg.Nodes.Select(nd => nd.Label).Contains("Rectangle 2") && wg.Find(e3.Id) is null, "whiteboard graph: only the connected elements, labelled by text or kind");
main.OpenProject(entry);
main.ShowWhiteboardCommand.Execute(null);
var wbVm = main.Board!.Whiteboard!;
wbVm.ToggleGraphCommand.Execute(null);
Check(wbVm.IsGraphMode && wbVm.Graph.Nodes.Count == wbVm.Data.Connectors.SelectMany(c => new[] { c.FromId, c.ToId }).Distinct().Count(), "whiteboard graph mode");
if (wbVm.Graph.Nodes.Count > 0)
{
    var first = wbVm.Graph.Nodes[0];
    wbVm.SelectGraphNode(first.Id);
    Check(wbVm.HasGraphSelection && wbVm.GraphElement?.Id == first.Id && wbVm.GraphInfo.Contains("linked to"), "whiteboard graph: node preview");
    wbVm.ShowOnBoardCommand.Execute(null);
    Check(wbVm.IsBoardMode && wbVm.SelectedIds.SetEquals(new[] { first.Id }), "whiteboard graph: show on board selects the element");
}
main.ShowKanbanCommand.Execute(null);

// ============================================================================ v1.3
V13Tests.Logic(Check, root);
V13Tests.ViewModels(Check, main, projects, dialogs, luca);
V13Tests.Review(Check, main, projects, dialogs, luca);

projects.Delete(docsEntry.Folder);
Check(Directory.Exists(Path.Combine(docsEntry.Folder, "docs")), "deleting a project keeps its documents");

// delete project
main.ShowProjects();
File.WriteAllText(Path.Combine(entry.Folder, "notes.txt"), "user file");
projects.Delete(entry.Folder);
Check(!File.Exists(Path.Combine(entry.Folder, "kanban.json")) && File.Exists(Path.Combine(entry.Folder, "notes.txt")), "delete removes app files only, keeps foreign files");
Check(!Directory.Exists(Path.Combine(entry.Folder, "whiteboards")), "delete removes the whiteboards too");
Check(projects.List().Count == 0, "project removed from list");

try { Directory.Delete(root, true); Directory.Delete(root2, true); } catch { }
Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures;

// ---------------------------------------------------------------- markdown test helpers

static string DumpInlines(IEnumerable<MdInline> inlines) =>
    string.Join("", MdDoc.Normalize(inlines).Select(r => r.Kind switch
    {
        MdInlineKind.LineBreak => "<br>",
        MdInlineKind.Image => $"<img {r.Source}|{r.Text}|{r.Href}>",
        _ => $"<{(r.Bold ? "b" : "")}{(r.Italic ? "i" : "")}{(r.Strike ? "s" : "")}{(r.Code ? "c" : "")}{(r.Href is null ? "" : (r.IsWiki ? " wiki=" : " href=") + r.Href)}>{r.Text}</>"
    }));

static string Dump(IEnumerable<MdBlock> blocks, string indent = "")
{
    var sb = new System.Text.StringBuilder();
    foreach (var block in blocks)
    {
        switch (block)
        {
            case MdFrontMatter f: sb.Append(indent).Append("FRONT ").AppendLine(f.Raw.Replace("\n", "\\n")); break;
            case MdHeading h: sb.Append(indent).Append($"H{h.Level} ").AppendLine(DumpInlines(h.Inlines)); break;
            case MdParagraph p: sb.Append(indent).Append("P ").AppendLine(DumpInlines(p.Inlines)); break;
            case MdCodeBlock c: sb.Append(indent).Append($"CODE[{c.Language}] ").AppendLine(c.Code.Replace("\n", "\\n")); break;
            case MdRule: sb.Append(indent).AppendLine("RULE"); break;
            case MdQuote q: sb.Append(indent).AppendLine("QUOTE"); sb.Append(Dump(q.Blocks, indent + "  ")); break;
            case MdList l:
                sb.Append(indent).AppendLine(l.Ordered ? $"OL start={l.Start}" : "UL");
                foreach (var item in l.Items)
                {
                    sb.Append(indent).Append("  ITEM ").AppendLine(item.Checked is null ? "" : item.Checked.Value ? "[x]" : "[ ]");
                    sb.Append(Dump(item.Blocks, indent + "    "));
                }
                break;
            case MdTable t:
                sb.Append(indent).AppendLine("TABLE " + string.Join(",", t.Align));
                sb.Append(indent).AppendLine("  HEAD " + string.Join(" | ", t.Header.Select(DumpInlines)));
                foreach (var row in t.Rows) sb.Append(indent).AppendLine("  ROW " + string.Join(" | ", row.Select(DumpInlines)));
                break;
        }
    }
    return sb.ToString();
}

static string RandomText(Random rng, bool tricky = true)
{
    string[] words = { "alpha", "beta", "x", "y2", "snake_case", "a", "Zed", "42", "né" };
    string[] symbols = { "*", "_", "**", "__", "[", "]", "`", "~", "~~", "\\", "#", "-", ">", "|", "<", "!", "(", ")", "+", "1.", "=", ":", "/", "&", "<b>", "![", "]]", "[[", "." };
    var sb = new System.Text.StringBuilder();
    var count = rng.Next(1, 5);
    for (var i = 0; i < count; i++)
    {
        if (i > 0 && rng.Next(4) != 0) sb.Append(' ');
        sb.Append(tricky && rng.Next(4) == 0 ? symbols[rng.Next(symbols.Length)] : words[rng.Next(words.Length)]);
    }
    if (rng.Next(6) == 0) sb.Insert(0, ' ');
    if (rng.Next(6) == 0) sb.Append(' ');
    return sb.ToString();
}

static List<MdInline> RandomInlines(Random rng, bool allowBreaks, bool allowLinks = true)
{
    var list = new List<MdInline>();
    var count = rng.Next(1, 6);
    for (var i = 0; i < count; i++)
    {
        var kind = rng.Next(20);
        if (kind == 0 && allowBreaks && list.Count > 0) { list.Add(MdInline.Break()); continue; }
        if (kind == 1) { list.Add(new MdInline { Kind = MdInlineKind.Image, Text = RandomText(rng).Trim(), Source = rng.Next(2) == 0 ? ".assets/pic 1.png" : "img/a(b).png" }); continue; }
        if (kind == 2 && allowLinks)
        {
            var target = (rng.Next(2) == 0 ? "Some Page " : "Folder/Other#Part ") + list.Count;
            list.Add(new MdInline { Text = rng.Next(2) == 0 ? target : "label " + rng.Next(9), Href = target, IsWiki = true, Bold = rng.Next(4) == 0 });
            continue;
        }
        var run = new MdInline { Text = RandomText(rng), Bold = rng.Next(3) == 0, Italic = rng.Next(3) == 0, Strike = rng.Next(6) == 0 };
        if (rng.Next(6) == 0) { run.Code = true; run.Text = RandomText(rng).Replace("\n", " "); }
        if (allowLinks && rng.Next(6) == 0) run.Href = rng.Next(2) == 0 ? "https://example.com/a_b?x=1" : "Docs/My File.md";
        list.Add(run);
    }
    // Known limit of markdown itself: italic that starts or ends in the middle of a word, right next to
    // other formatting, is ambiguous ("**a***b*c"). People do not write that: keep italic on word boundaries.
    for (var i = 0; i < list.Count; i++)
    {
        var run = list[i];
        if (run.Kind != MdInlineKind.Text || !run.Italic || run.IsWiki) continue;
        if (i > 0 && list[i - 1] is { Kind: MdInlineKind.Text, Italic: false, Code: false, IsWiki: false } before && before.Href == run.Href &&
            before.Text.Length > 0 && char.IsLetterOrDigit(before.Text[^1]) && !run.Text.StartsWith(' '))
            before.Text += " ";
        if (i + 1 < list.Count && list[i + 1] is { Kind: MdInlineKind.Text, Italic: false, Code: false, IsWiki: false } after && after.Href == run.Href &&
            after.Text.Length > 0 && char.IsLetterOrDigit(after.Text[0]) && !run.Text.EndsWith(' '))
            after.Text = " " + after.Text;
    }
    // at least one visible character
    if (MdDoc.Normalize(list).All(r => r.Kind != MdInlineKind.Text)) list.Add(MdInline.Plain("text"));
    return list;
}

static List<MdBlock> RandomBlocks(Random rng, int depth)
{
    var blocks = new List<MdBlock>();
    var count = rng.Next(1, depth == 0 ? 6 : 3);
    string? last = null;
    for (var i = 0; i < count; i++)
    {
        var kind = rng.Next(depth >= 2 ? 4 : 9);
        MdBlock block;
        switch (kind)
        {
            case 1:
                block = new MdHeading { Level = rng.Next(1, 7), Inlines = RandomInlines(rng, allowBreaks: false) };
                break;
            case 2:
            {
                var lines = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => rng.Next(5) == 0 ? "" : (rng.Next(3) == 0 ? "    " : "") + RandomText(rng).TrimEnd() + (rng.Next(6) == 0 ? " ```" : "")).ToList();
                if (lines.All(l => l.Length == 0)) lines.Add("code");
                block = new MdCodeBlock { Language = rng.Next(2) == 0 ? "" : "csharp", Code = string.Join("\n", lines) };
                break;
            }
            case 3:
                block = new MdRule();
                break;
            case 4:
                if (last == "quote") goto default;
                block = new MdQuote { Blocks = RandomBlocks(rng, depth + 1) };
                break;
            case 5:
            case 6:
            {
                var ordered = kind == 6;
                var list = new MdList { Ordered = ordered, Start = ordered ? rng.Next(1, 4) : 1 };
                var items = rng.Next(1, 4);
                var isTask = rng.Next(4) == 0;
                for (var k = 0; k < items; k++)
                {
                    var item = new MdListItem { Checked = isTask ? rng.Next(2) == 0 : null };
                    item.Blocks.Add(new MdParagraph { Inlines = RandomInlines(rng, allowBreaks: true) });
                    if (rng.Next(4) == 0) item.Blocks.Add(new MdList { Ordered = !ordered, Items = { new MdListItem { Blocks = { new MdParagraph { Inlines = RandomInlines(rng, false) } } } } });
                    else if (rng.Next(6) == 0) item.Blocks.Add(new MdParagraph { Inlines = RandomInlines(rng, true) });
                    else if (rng.Next(8) == 0) item.Blocks.Add(new MdCodeBlock { Code = "x = 1\n\ny = 2" });
                    list.Items.Add(item);
                }
                block = list;
                break;
            }
            case 7:
            {
                var columns = rng.Next(1, 4);
                var t = new MdTable();
                for (var c = 0; c < columns; c++) { t.Align.Add((MdAlign)rng.Next(4)); t.Header.Add(RandomInlines(rng, false)); }
                for (var r = rng.Next(0, 3); r > 0; r--)
                    t.Rows.Add(Enumerable.Range(0, columns).Select(_ => rng.Next(5) == 0 ? new List<MdInline>() : RandomInlines(rng, rng.Next(3) == 0)).ToList());
                block = t;
                break;
            }
            default:
                block = new MdParagraph { Inlines = RandomInlines(rng, allowBreaks: true) };
                break;
        }
        last = block switch { MdQuote => "quote", MdList { Ordered: true } => "ol", MdList => "ul", _ => null };
        blocks.Add(block);
    }
    return blocks;
}


class XorProtector : IDataProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] encrypted) => Protect(encrypted);
}

class FakeDialogs : IDialogService
{
    public Func<DialogViewModel, bool>? OnDialog;
    public string PromptAnswer = "";
    public bool ConfirmAnswer = true;
    public bool? ShowDialog(DialogViewModel vm)
    {
        if (OnDialog != null && OnDialog(vm)) return true;
        if (vm is PromptViewModel p) { p.Value = PromptAnswer; return true; }
        return false;
    }
    public bool Confirm(string message, string title = "Confirm") => ConfirmAnswer;
    public void Info(string message, string title = "") => Infos.Add(message);
    public bool QuietErrors;
    public void Error(string message, string title = "Error")
    {
        Errors.Add(message);
        if (!QuietErrors) Console.WriteLine("  [error dialog] " + message.Replace("\n", " "));
    }
    public string? PickFolder(string? initialFolder = null) => null;
    public void OpenFolder(string folder) => Folders.Add(folder);
    public string? PickFile(string filter) => FileAnswer;
    public string? PickSaveFile(string defaultName, string filter) => SaveFileAnswer;
    public string? SaveFileAnswer;
    public List<string> FilesAnswer = new();
    public IReadOnlyList<string> PickFiles(string filter) => FilesAnswer;
    public string? ImageAnswer;
    public string? PickSquareImage(int size = 128) => ImageAnswer;
    public bool? AskAnswer = true;
    public bool? Ask(string message, string title) => AskAnswer;
    public List<string> Opened = new();
    public void OpenExternal(string target) => Opened.Add(target);
    public List<WindowViewModel> Windows = new();
    public void ShowWindow(WindowViewModel vm)
    {
        if (Windows.Contains(vm)) return;
        Windows.Add(vm);
        vm.OnOpened();
        vm.CloseRequested += () => { if (Windows.Remove(vm)) vm.OnClosed(); };
    }
    public List<string> Errors = new();
    public List<string> Folders = new();
    public string? FileAnswer;
    public List<string> Infos = new();
}

class FakeTheme : IThemeService
{
    public bool IsDark { get; private set; }
    public void Apply(bool dark) => IsDark = dark;
}

class FakeSound : ISoundService
{
    public List<string> Played = new();
    public void Play(string sound, int volume, string? customFile = null) => Played.Add($"{sound}@{volume}");
}
