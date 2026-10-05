using HighshoreCairn.Models;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;

/// <summary>Checks of the v1.3 code that needs no project on screen (icons, roadmap rules, rich text, references).</summary>
static class V13Tests
{
    public static void Logic(Action<bool, string> Check, string root)
    {
        Console.WriteLine("== v1.3: icons (SVG)");
        var repoRoot = AppContext.BaseDirectory;
        while (repoRoot != null && !File.Exists(Path.Combine(repoRoot, "HighshoreCairn.sln"))) repoRoot = Path.GetDirectoryName(repoRoot);
        var iconZip = Path.Combine(repoRoot!, "src", "HighshoreCairn", "Assets", "ionicons.zip");
        IconLibrary icons;
        using (var zipStream = File.OpenRead(iconZip)) icons = new IconLibrary(zipStream);
        Check(icons.Names.Count == 1356 && icons.Contains("rocket") && icons.Contains("bug-outline") && !icons.Contains("LICENSE"), "the built-in icon set is complete (1356 icons)");
        var badIcons = icons.Names.Where(n => icons.Get(n) is not { Shapes.Count: > 0 }).ToList();
        Check(badIcons.Count == 0, "every icon is parsed" + (badIcons.Count > 0 ? ": " + string.Join(", ", badIcons.Take(8)) : ""));
        var allowed = new HashSet<char>("MLCQAZ0123456789.- ");
        Check(icons.Names.All(n => icons.Get(n)!.Shapes.All(sh => sh.Path.All(allowed.Contains) && sh.Path.StartsWith("M "))), "icon paths only use absolute M L C Q A Z commands");
        Check(icons.Names.Where(n => n.EndsWith("-outline")).Count(n => icons.Get(n)!.Shapes.Any(sh => sh.Stroke && sh.StrokeWidth >= 10)) > 400 &&
              icons.Get("rocket")!.Shapes.All(sh => sh.Fill && !sh.Stroke), "outline icons are stroked, filled icons are filled");
        Check(MilestoneType.Defaults().All(t => icons.Contains(IconLibrary.BuiltInName(t.Icon)!)) && icons.Contains(IconLibrary.BuiltInName(MilestoneType.DefaultIcon)!), "the default milestone icons exist");
        var svg1 = SvgParser.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M2 2h4v4H2zm10-1.5.5.5l-1 1' /><circle cx='12' cy='12' r='3' style='fill:none;stroke:#000;stroke-width:2px;stroke-linecap:round'/><rect x='1' y='1' width='10' height='6' rx='2' fill='none' stroke='currentColor'/><line x1='0' y1='0' x2='5' y2='5' stroke='black'/><g fill='none'><polygon points='1,1 2,2 3,1'/></g></svg>");
        Check(svg1.Width == 24 && svg1.Shapes.Count == 4, "svg: viewBox and shapes (an invisible shape is skipped)");
        Check(svg1.Shapes[0].Path == "M 2 2 L 6 2 L 6 6 L 2 6 Z M 12 0.5 L 12.5 1 L 11.5 2" && svg1.Shapes[0].Fill && !svg1.Shapes[0].Stroke, "svg: relative commands, implicit lines and compact numbers become absolute");
        Check(svg1.Shapes[1].Path == "M 9 12 A 3 3 0 1 0 15 12 A 3 3 0 1 0 9 12 Z" && svg1.Shapes[1].Stroke && !svg1.Shapes[1].Fill && svg1.Shapes[1].StrokeWidth == 2 && svg1.Shapes[1].RoundCap, "svg: circle and inline style");
        Check(svg1.Shapes[2].Path.StartsWith("M 3 1 L 9 1 A 2 2 0 0 1 11 3") && svg1.Shapes[3].Path == "M 0 0 L 5 5" && !svg1.Shapes[3].Fill, "svg: rounded rectangle and line");
        var svg2 = SvgParser.Parse("<svg viewBox='0 0 10 10'><path d='M0 0a1 1 0 011 1s1 1 2 0T5 5' transform='rotate(90 5 5)'/></svg>");
        Check(svg2.Shapes[0].Path.StartsWith("M 10 0 A 1 1 90 0 1 9 1 C 9 1 8 2 9 3"), "svg: compact arc flags, smooth curves, rotation");
        foreach (var broken in new[] { "", "<html/>", "<svg><path d='M 1'/></svg>", "<svg><path d='X 1 1'/></svg>", "<svg><path d='1 1'/></svg>", "not xml" })
        {
            var threw = false;
            try { SvgParser.Parse(broken); } catch (Exception ex) when (ex is FormatException or System.Xml.XmlException) { threw = true; }
            Check(threw, $"svg: invalid input is rejected ({(broken.Length > 20 ? broken[..20] : broken)})");
        }

        Console.WriteLine("== v1.3: roadmap bars");
        var rs = new RoadmapSettings();
        DateTime D(int month, int day) => new(2026, month, day);
        string Segs(RoadmapBar? bar) => bar is null ? "none" : string.Join(" ", bar.Segments.Select(sg => $"{sg.Fill}:{sg.From:MMdd}-{sg.To:MMdd}"));
        var nowR = D(6, 15).AddHours(12);
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 1), FinishDate = D(3, 10), DueDate = D(3, 5), EndDate = D(3, 20) }, false, rs, nowR)) == "Outline:0301-0311", "finish date: one plain bar, due and end ignored");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 1), DueDate = D(3, 10), EndDate = D(3, 6) }, false, rs, nowR)) == "Dark:0301-0307 Light:0307-0311", "finished early: dark until the end, light until the due date");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 1), DueDate = D(3, 10), EndDate = D(3, 15) }, false, rs, nowR)) == "Dark:0301-0311 Striped:0311-0316", "finished late: striped after the due date");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 12), DueDate = D(3, 10), EndDate = D(3, 15) }, false, rs, nowR)) == "Striped:0311-0312 Dark:0312-0316", "started after the due date: striped up to the start");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 1), EndDate = D(3, 4) }, false, rs, nowR)) == "Dark:0301-0305", "start and end without due date: plain dark bar");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 1), DueDate = D(3, 10), DueHasTime = true, EndDate = D(3, 10).AddHours(18), EndHasTime = true }, false, rs, nowR)) == "Dark:0301-0310 Striped:0310-0310", "times are respected (late by a few hours)");
        Check(RoadmapLayout.CardBar(new CardData { DueDate = D(3, 10), EndDate = D(3, 9) }, false, rs, nowR) is null &&
              RoadmapLayout.CardBar(new CardData { StartDate = D(3, 1) }, false, rs, nowR) is null, "no start date, or nothing but the start: not in the roadmap");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(6, 1), DueDate = D(6, 30) }, false, rs, nowR)) == "Dark:0601-0615 Light:0615-0701", "in progress: dark up to now, light up to the due date");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(5, 1), DueDate = D(5, 30) }, false, rs, nowR)) == "Dark:0501-0531 Striped:0531-0615", "in progress and overdue: striped up to now");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(7, 1), DueDate = D(7, 30) }, false, rs, nowR)) == "Light:0701-0731", "planned (not started yet): light");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(5, 1), DueDate = D(5, 30) }, true, rs, nowR)) == "Dark:0501-0531", "in a done column without an end date: assumed on time");
        Check(RoadmapLayout.CardBar(new CardData { StartDate = D(6, 1), DueDate = D(6, 30) }, false, new RoadmapSettings { ShowInProgress = false }, nowR) is null, "in progress tasks can be hidden");
        Check(Segs(RoadmapLayout.CardBar(new CardData { StartDate = D(3, 5), EndDate = D(3, 1) }, false, rs, nowR)) == "Dark:0305-0306", "an end before the start still gives a one-day bar");
        Check(Segs(RoadmapLayout.ItemBar(new RoadmapItem { Start = D(3, 1), End = D(3, 9) }, nowR)) == "Outline:0301-0310" &&
              Segs(RoadmapLayout.ItemBar(new RoadmapItem { Start = D(3, 1), Due = D(3, 5), End = D(3, 9) }, nowR)) == "Dark:0301-0306 Striped:0306-0310" &&
              RoadmapLayout.ItemBar(new RoadmapItem { Start = D(3, 1) }, nowR) is null, "custom tasks: without a due date the end acts as a finish date");

        RoadmapBar Bar(string id, int from, int to) { var b = new RoadmapBar { Id = id, Title = id }; b.Segments.Add(new RoadmapSegment { From = D(1, from), To = D(1, to) }); return b; }
        var lanes = RoadmapLayout.Lanes(new[] { Bar("c", 10, 20), Bar("a", 1, 5), Bar("b", 3, 12), Bar("d", 5, 9), Bar("e", 12, 14) });
        Check(string.Join(" | ", lanes.Select(l => string.Join(",", l.Select(b => b.Id)))) == "a,d,c | b,e", "overlapping bars go to different lanes, the others share one");

        Console.WriteLine("== v1.3: milestones");
        Milestone Ms(string id, int from, int to, string type = "major") => new() { Id = id, Name = id, TypeId = type, Start = D(1, from), End = D(1, to) };
        string Ids(IEnumerable<Milestone> list) => string.Join(",", list.Select(m => m.Id).OrderBy(x => x));
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 3, 6) })) == "b", "a milestone inside another one is hidden");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 5, 20), Ms("c", 3, 15) })) == "c", "a milestone in the middle of two overlapping ones is hidden");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 4), Ms("b", 5, 20), Ms("c", 3, 15) })) == "", "...but not when the two outer ones do not overlap");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 8, 20), Ms("c", 18, 30) })) == "", "a chain of overlaps hides nothing");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 1, 10), Ms("c", 1, 12) })) == "a,b", "same start: the shorter ones are hidden; duplicates too");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 1, 10) })) == "b", "of two identical milestones the first one stays");
        var msAll = new List<Milestone> { Ms("a", 1, 10), Ms("b", 20, 25), Ms("x", 1, 3, "beta") };
        var levels = new RoadmapSettings();
        levels.MilestoneTypes.First(t => t.Id == "beta").Level = 3;
        Check(MilestoneRules.Check(Ms("n", 1, 10), msAll, levels).Identical?.Id == "a", "same dates on the same level: not allowed");
        Check(MilestoneRules.Check(Ms("n", 1, 3), msAll, levels) is { Identical: null } inside && Ids(inside.Hidden) == "n", "a new milestone that would be hidden is reported");
        Check(Ids(MilestoneRules.Check(Ms("n", 1, 30), msAll, levels).Hidden) == "a,b", "a new milestone that hides others is reported");
        Check(MilestoneRules.Check(Ms("n", 1, 3, "beta"), msAll.Where(m => m.Id != "x").ToList(), levels) is { Identical: null, Hidden.Count: 0 }, "milestones of another level never interfere");
        Check(MilestoneRules.Check(Ms("a", 2, 11), msAll, levels) is { Identical: null, Hidden.Count: 0 }, "editing a milestone does not compare it with itself");

        var rd = new RoadmapData { Milestones = { Ms("a", 1, 10), Ms("b", 8, 20, "minor"), Ms("c", 3, 5), Ms("z", 2, 4, "beta") } };
        var mLanes = RoadmapLayout.MilestoneLanes(rd, levels);
        Check(mLanes.Count == 2 && string.Join(",", mLanes[0].Select(b => b.Id)) == "a,b" && mLanes[1].Single().Id == "z", "one line per level in use; hidden milestones are left out");
        Check(Segs(mLanes[0][0]) == "Dark:0101-0108" && Segs(mLanes[0][1]) == "Mixed:0108-0111 Dark:0111-0121" && mLanes[0][1].Segments[0].Color2 == mLanes[0][0].Color && mLanes[0][1].LabelFrom == D(1, 11), "the shared days are painted with both colors");
        Check(mLanes[0][0].Icon == "ion:rocket" && mLanes[0][1].Color == levels.MilestoneTypes.First(t => t.Id == "minor").Color, "milestones take icon and color from their type");

        // found by the review: a milestone that lies inside another one cannot be what hides it
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 5, 15), Ms("c", 1, 12) })) == "a", "same start day: only the shorter milestone is hidden");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 5, 15), Ms("c", 8, 15) })) == "c", "same end day: only the shorter milestone is hidden");
        Check(Ids(MilestoneRules.Covered(new[] { Ms("a", 1, 10), Ms("b", 1, 5), Ms("c", 5, 10) })) == "b,c", "two halves of a longer milestone are hidden, the long one stays");
        Check(Ids(MilestoneRules.Check(Ms("c", 1, 12), new List<Milestone> { Ms("a", 1, 10), Ms("b", 5, 15) }, levels).Hidden) == "a", "a new milestone that starts with another one hides it, not itself");
        Check(Ids(MilestoneRules.Hidden(new[] { Ms("a", 1, 10), Ms("b", 3, 6), Ms("x", 1, 10, "beta"), Ms("y", 2, 3, "beta"), Ms("z", 2, 3, "minor") }, levels)) == "b,y,z" &&
              Ids(MilestoneRules.Hidden(new[] { Ms("a", 1, 10), Ms("y", 2, 3, "beta") }, levels)) == "", "hidden milestones are found level by level");
        var both = RoadmapLayout.MilestoneLanes(new RoadmapData { Milestones = { Ms("a", 1, 5), Ms("b", 3, 10, "minor"), Ms("c", 6, 12, "bugfix") } }, levels)[0];
        Check(both.Count == 3 && Segs(both[1]) == "Mixed:0103-0106" && both[1].LabelFrom == D(1, 3) && Segs(both[2]) == "Mixed:0106-0111 Dark:0111-0113",
            "a milestone shared on both sides keeps its name over the shared days");

        Console.WriteLine("== v1.3: roadmap rows and timeline");
        var rb = new BoardData { Tags = { new TagDef { Id = "t1", Name = "Bug" }, new TagDef { Id = "t2", Name = "Feature" }, new TagDef { Id = "t3", Name = "Design" } } };
        rb.Columns.Add(new ColumnData { Name = "Doing", Cards =
        {
            new CardData { Id = "k1", Title = "One", TagIds = { "t1" }, StartDate = D(1, 1), EndDate = D(1, 5) },
            new CardData { Id = "k2", Title = "Two", TagIds = { "t1", "t2" }, StartDate = D(1, 3), EndDate = D(1, 8) },
            new CardData { Id = "k3", Title = "Three", StartDate = D(1, 3), FinishDate = D(1, 8) },
            new CardData { Id = "k4", Title = "No dates", TagIds = { "t3" } }
        } });
        var rdata = new RoadmapData { Items = { new RoadmapItem { Id = "i1", Name = "Custom one", Start = D(2, 1), End = D(2, 3) } } };
        var rrows = RoadmapLayout.Build(rb, rdata, new RoadmapSettings(), nowR);
        Check(string.Join(",", rrows.Select(r => r.Key)) == "milestones,custom,t1,t2,t3,untagged", "rows: milestones, custom, one per tag, then the tasks without a tag");
        Check(rrows[2].Lanes.Count == 2 && rrows[3].Count == 1 && rrows[4].Count == 0 && rrows[5].Lanes[0][0].Id == "k3" && rrows[1].Lanes[0][0].Title == "Custom one", "a task appears in the row of each of its tags; rows grow by lanes");
        Check(RoadmapLayout.MoveTag(rb, rdata, "t3", -1) && !RoadmapLayout.MoveTag(rb, rdata, "t1", -1) && string.Join(",", rdata.TagOrder) == "t1,t3,t2", "tag rows can be reordered");
        rb.Tags.Add(new TagDef { Id = "t4", Name = "New" });
        rdata.Collapsed.Add("t3");
        rrows = RoadmapLayout.Build(rb, rdata, new RoadmapSettings { ShowMilestones = false, ShowCustom = false, CustomName = "x" }, nowR);
        Check(string.Join(",", rrows.Select(r => r.Key)) == "t1,t3,t2,t4,untagged" && rrows[1].Collapsed && !rrows[0].Collapsed, "new tags get a row at the end; rows can be hidden and collapsed");
        Check(RoadmapLayout.PlaceTag(rb, rdata, "t4", 0) && string.Join(",", rdata.TagOrder) == "t4,t1,t3,t2" && !RoadmapLayout.PlaceTag(rb, rdata, "t4", 0), "a tag row can be dropped at a position");

        var edge = new RoadmapTimeline(new DateTime(3, 1, 15), 12);
        for (var i = 0; i < 40; i++) edge.Previous();
        var far = new RoadmapTimeline(new DateTime(9989, 6, 1), 12);
        for (var i = 0; i < 40; i++) far.Next();
        Check(edge.Year == 2 && edge.From.Year == 2 && far.Year == 9990 && far.To.Year == 9991, "the timeline stops before the dates run out");

        var tl = new RoadmapTimeline(new DateTime(2026, 10, 4), 6);
        Check(tl.Year == 2026 && tl.StartMonth == 7 && tl.Label == "Jul – Dec 2026", "today: the current month as far left as the year allows");
        tl.Next();
        Check(tl.Year == 2027 && tl.StartMonth == 1 && tl.Label == "Jan – Jun 2027", "right from December: next year, January on the left");
        tl.Previous();
        Check(tl.Year == 2026 && tl.StartMonth == 7, "left from January: previous year, December on the right");
        tl.Previous(); tl.Previous();
        Check(tl.StartMonth == 5 && tl.From == new DateTime(2026, 5, 1) && tl.To == new DateTime(2026, 11, 1), "the arrows move one month at a time");
        tl.SetMonths(1);
        Check(tl.Label == "May 2026" && tl.Months == 1, "one month: its full name");
        tl.SetMonths(12);
        Check(tl.StartMonth == 1 && tl.Label == "Jan – Dec 2026", "twelve months: the whole year");
        tl.Next(); tl.SetMonths(40); tl.SetMonths(0);
        Check(tl.Year == 2027 && tl.Months == 1, "the month count stays between 1 and 12");
        tl.SetMonths(3); tl.Today(new DateTime(2026, 3, 9));
        Check(tl.Label == "Mar – May 2026", "today with a short range");
        Check(RoadmapLayout.FormatHover(new DateTime(2026, 5, 2), true) == "02 May 2026 - 18w" && RoadmapLayout.FormatHover(new DateTime(2026, 5, 2), false) == "02 May 2026", "date under the mouse, with the optional week number");

        Console.WriteLine("== v1.3: rich text helpers");
        var rtfSample = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil Segoe UI;}{\f1 Arial;}}{\colortbl ;\red255\green0\blue0;}{\*\generator Riched20}\pard\f0\fs24 Hello \b bold\b0  w\'f6rld\par Second §8364? line \{x\}\line{\pict\wmetafile8 0102030405}after\par}".Replace("§", "\\" + "u");
        Check(RtfText.ToPlain(rtfSample) == "Hello bold wörld\nSecond € line {x}\nafter\n", "rtf to plain text: formatting, tables and pictures dropped, characters decoded");
        Check(RtfText.ToPlain("plain text") == "plain text" && RtfText.ToPlain(null) == "" && RtfText.ToPlain(DocsService.EmptyRtf) == "" && RtfText.LooksLikeRtf(DocsService.EmptyRtf), "rtf to plain text: empty and non-rtf input");
        foreach (var nastyRtf in new[] { @"{\rtf1", @"{\rtf1 \", @"{\rtf1 \'", @"{\rtf1 \u", @"{\rtf1 }}}}", @"{\rtf1 \'zz \u-10 ok}" })
        {
            try { RtfText.ToPlain(nastyRtf); Check(true, "rtf to plain text survives broken input"); }
            catch (Exception ex) { Check(false, "rtf to plain text survives broken input: " + ex.GetType().Name); }
        }

        Console.WriteLine("== v1.3: whiteboard references");
        var lw = new WhiteboardData();
        var le1 = new WbElement { Kind = WbKind.Rectangle }; var le2 = new WbElement { Kind = WbKind.Ellipse }; var le3 = new WbElement { Kind = WbKind.Text, Text = "Note" };
        lw.Elements.AddRange(new[] { le1, le2, le3 });
        Check(WhiteboardLinks.LinkCard(lw, le1.Id, "card1") && WhiteboardLinks.LinkCard(lw, le1.Id, "card2") && !WhiteboardLinks.LinkCard(lw, le1.Id, "card1") && le1.CardRefs!.Count == 2, "an element can reference several tasks");
        Check(WhiteboardLinks.LinkCard(lw, le2.Id, "card1") && le1.CardRefs!.SequenceEqual(new[] { "card2" }) && WhiteboardLinks.ElementOfCard(lw, "card1") == le2, "a task is linked to one element only: a new link replaces the old one");
        Check(WhiteboardLinks.LinkDoc(lw, le2.Id, "Design/Combat.md") && !WhiteboardLinks.LinkDoc(lw, le2.Id, "design/combat.md") && le2.HasRefs && !le3.HasRefs, "document references");
        Check(WhiteboardLinks.RemapDocs(lw, "Design", "Game Design") && le2.DocRefs![0] == "Game Design/Combat.md" && !WhiteboardLinks.RemapDocs(lw, "Design", "X") &&
              WhiteboardLinks.Remap("Design.md", "Design", "X") is null, "references follow renamed documents and folders");
        le3.Favorite = true;
        var lg = WhiteboardGraph.Build(lw);
        Check(lg.Nodes.Count == 3 && lg.Find(le1.Id)!.HasDot && !lg.Find(le3.Id)!.HasDot && lg.Find(le3.Id)!.Starred, "graph: elements with references or a star are shown, with their marks");
        le3.Favorite = false;
        Check(WhiteboardGraph.Build(lw).Find(le3.Id) is null && WhiteboardGraph.Build(lw, new[] { le3.Id }).Find(le3.Id) != null, "graph: an unlinked element is shown on request (View in Graph)");
        var copyOfLe1 = WhiteboardOps.Duplicate(lw, new[] { le1.Id })[0];
        Check(copyOfLe1.CardRefs is null && le1.CardRefs!.Count == 1, "a duplicated element does not take the task links");
        WhiteboardOps.AddConnector(lw, le1.Id, le2.Id); WhiteboardOps.AddConnector(lw, le2.Id, le3.Id);
        Check(WhiteboardOps.Disconnect(lw, new[] { le1.Id }) == 1 && lw.Connectors.Count == 1, "disconnect removes the connectors of the element");
        le1.Favorite = true;
        var merged = WhiteboardOps.Group(lw, new[] { le1.Id, le2.Id })!;
        Check(merged.Favorite && merged.CardRefs!.OrderBy(x => x).SequenceEqual(new[] { "card1", "card2" }) && merged.DocRefs!.Count == 1 &&
              WhiteboardLinks.ElementOfCard(lw, "card1") == merged && WhiteboardLinks.ElementOfCard(lw, "card2") == merged, "merge: star and references belong to the group");
        WhiteboardLinks.LinkCard(lw, merged.Id, "card9");
        WhiteboardLinks.LinkDoc(lw, merged.Id, "Later.md");
        var parts = WhiteboardOps.Ungroup(lw, merged.Id);
        Check(parts.Count == 2 && le1.Favorite && !le2.Favorite && le1.CardRefs!.Contains("card2") && !le1.CardRefs.Contains("card1") &&
              le2.CardRefs!.SequenceEqual(new[] { "card1" }) && le2.DocRefs!.SequenceEqual(new[] { "Game Design/Combat.md" }), "split: every element gets back its own star and references");
        Check(parts[0].CardRefs!.Contains("card9") && parts[0].DocRefs!.Contains("Later.md") && !parts[1].CardRefs!.Contains("card9"), "split: what was added to the whole goes to the first element");
        var regroup = WhiteboardOps.Group(lw, new[] { le1.Id, le2.Id })!;
        WhiteboardLinks.UnlinkCard(lw, "card1"); WhiteboardLinks.UnlinkDoc(lw, regroup.Id, "game design/combat.md"); regroup.Favorite = false;
        WhiteboardOps.Ungroup(lw, regroup.Id);
        Check(le2.CardRefs is null && le2.DocRefs is null && !le1.Favorite && lw.Elements.SelectMany(e => e.DocRefs ?? new List<string>()).SequenceEqual(new[] { "Later.md" }),
            "split: a star or reference removed while merged stays removed");
        WhiteboardLinks.LinkCard(lw, le2.Id, "card1"); WhiteboardLinks.LinkDoc(lw, le2.Id, "Game Design/Combat.md");
        Check(WhiteboardLinks.UnlinkCard(lw, "card1") && !WhiteboardLinks.UnlinkCard(lw, "card1") && WhiteboardLinks.UnlinkDoc(lw, le2.Id, "game design/combat.md") && le2.DocRefs is null, "references can be removed");

        // references are not part of the undo history
        var past = JsonFile.Clone(lw);
        var gone = new WbElement { Id = "gone", Kind = WbKind.Text, Text = "Deleted earlier", CardRefs = new List<string> { "cardA", "cardB", "card2" }, DocRefs = new List<string> { "Old.md" } };
        past.Elements.Add(gone);
        WhiteboardLinks.LinkCard(lw, le3.Id, "cardNew");
        WhiteboardLinks.CarryReferences(lw, past, () => new HashSet<string> { "cardB" });
        Check(past.Elements.First(e => e.Id == le3.Id).CardRefs!.SequenceEqual(new[] { "cardNew" }) && past.Elements.First(e => e.Id == le1.Id).CardRefs!.Contains("card2"),
            "undo keeps the references the elements have now");
        Check(gone.CardRefs!.SequenceEqual(new[] { "cardA" }) && gone.DocRefs!.Single() == "Old.md",
            "an element that comes back keeps its references, except the tasks linked elsewhere in the meantime");
        Check(WhiteboardLinks.RemoveDocs(past, "Old.md") && gone.DocRefs is null && !WhiteboardLinks.RemoveDocs(past, "Old.md"), "references to a deleted document are removed");
        var folderRefs = new List<string> { "Design/A.md", "Design/Sub/B.rtf", "Designs.md", "design" };
        Check(WhiteboardLinks.RemoveDocs(folderRefs, "Design") && folderRefs.SequenceEqual(new[] { "Designs.md" }), "...also those inside a deleted folder");
        var linkRoot = Path.Combine(root, "links-test");
        var linkService = new WhiteboardService(linkRoot);
        var boardA = linkService.Create("A"); var boardB = linkService.Create("B");
        boardA.Elements.Add(new WbElement { Id = "ea", Kind = WbKind.Text, Text = "Alpha", CardRefs = new List<string> { "c1", "c2" }, DocRefs = new List<string> { "Old.md" } });
        boardB.Elements.Add(new WbElement { Id = "eb", Kind = WbKind.Rectangle, CardRefs = new List<string> { "c3" } });
        linkService.Save(boardA); linkService.Save(boardB);
        var cardIndex = WhiteboardLinks.CardIndex(linkService);
        Check(cardIndex.Count == 3 && cardIndex["c1"].Label == "Alpha" && cardIndex["c1"].BoardName == "A" && cardIndex["c3"].ElementId == "eb" && cardIndex["c3"].Label == "Rectangle 1", "index of the tasks referenced by the whiteboards");
        WhiteboardLinks.LinkCard(boardB, "eb", "c1");
        WhiteboardLinks.UnlinkCardElsewhere(linkService, "c1", boardB.Id);
        cardIndex = WhiteboardLinks.CardIndex(linkService, boardB);
        Check(cardIndex["c1"].ElementId == "eb" && linkService.Load(boardA.Id)!.Elements[0].CardRefs!.SequenceEqual(new[] { "c2" }), "linking a task on another whiteboard removes the old link there");
        WhiteboardLinks.RemapDocsOnDisk(linkService, "Old.md", "New.md", null);
        Check(linkService.Load(boardA.Id)!.Elements[0].DocRefs![0] == "New.md", "document references on disk follow a rename");
        var remapBoard = new BoardData { Columns = { new ColumnData { Cards = { new CardData { DocRefs = new List<string> { "Notes/A.md", "B.md" } } } } } };
        Check(WhiteboardLinks.RemapDocs(remapBoard, "Notes", "Archive/Notes") && remapBoard.Columns[0].Cards[0].DocRefs![0] == "Archive/Notes/A.md", "task references follow a moved folder");
    }

    /// <summary>A stand-in for the WPF converter: enough to check what the documentation does around a conversion.</summary>
    class FakeConverter : IDocConverter
    {
        public string MarkdownToRtf(string markdown, Func<string, string?> resolveImage) =>
            @"{\rtf1\ansi " + MdDoc.PlainText(MdDoc.Parse(markdown)).Replace("\n", @"\par ") + "}";
        public string RtfToMarkdown(string rtf, Func<byte[], string> saveImage) => RtfText.ToPlain(rtf);
    }

    public static void ViewModels(Action<bool, string> Check, MainViewModel main, ProjectService projects, FakeDialogs dialogs, Account owner)
    {
        Console.WriteLine("== v1.3: task dates and references");
        dialogs.OnDialog = null;
        dialogs.ConfirmAnswer = true;
        main.DocConverter = new FakeConverter();
        var entry = projects.Create("Roadmap Project", "", null, "#1F6F8B", owner);
        var docs = new DocsService(entry.Folder);
        var specPath = docs.CreateDocument("Design", "Spec", "# Spec\n\nSee [[Notes]].\n");
        var notesPath = docs.CreateDocument("", "Notes", "# Notes\n");
        main.OpenProject(entry);
        var board = main.Board!;
        var todo = board.Columns[0]; var done = board.Columns[2];
        DateTime Day(int month, int day) => new(2026, month, day);

        dialogs.OnDialog = vm =>
        {
            if (vm is ItemPickerViewModel picker) { picker.Selected = picker.Items.First(i => i.Id == specPath); picker.OkCommand.Execute(null); return true; }
            if (vm is not CardEditorViewModel ed) return false;
            ed.Title = "Build the editor";
            ed.TagOptions.First(t => t.Name == "Feature").IsSelected = true;
            ed.StartDate = Day(3, 2);
            ed.FinishDate = Day(3, 1);
            ed.SaveCommand.Execute(null);
            var rejected = ed.Result is null && ed.HasError;
            ed.FinishDate = null;
            ed.DueDate = Day(3, 20);
            ed.EndDate = Day(3, 25); ed.EndTime = "25:00";
            ed.SaveCommand.Execute(null);
            rejected &= ed.Result is null;
            ed.EndTime = "17.30";
            ed.AddReferenceCommand.Execute(null);
            ed.SaveCommand.Execute(null);
            return rejected && ed.Result != null;
        };
        todo.AddCardCommand.Execute(null);
        var card = todo.Data.Cards.Single();
        Check(card.StartDate == Day(3, 2) && card.FinishDate is null && card.EndDate == Day(3, 25).AddHours(17.5) && card.EndHasTime && card.HasRoadmapDates,
            "card dialog: start, finish and end date (finish before start and a wrong time are rejected)");
        Check(card.DocRefs!.SequenceEqual(new[] { specPath }), "card dialog: a document can be linked in References");
        var cardVm = todo.VisibleCards.Single();
        Check(cardVm.HasReferences && cardVm.References.Single() is { IsDocument: true, Label: "Spec", IsMissing: false }, "the card shows its references");

        dialogs.OnDialog = vm =>
        {
            if (vm is not CardEditorViewModel ed) return false;
            var had = ed.References.Count == 1 && ed.HasEndDate && ed.EndTime == "17:30" && ed.StartDate == Day(3, 2);
            ed.RemoveReferenceCommand.Execute(ed.References[0]);
            ed.ClearEndCommand.Execute(null);
            ed.ClearStartCommand.Execute(null);
            ed.SaveCommand.Execute(null);
            return had && ed.Result != null;
        };
        cardVm.EditCommand.Execute(null);
        card = todo.Data.Cards.Single();
        Check(card.DocRefs is null && card.EndDate is null && card.StartDate is null && !card.EndHasTime &&
              card.Activity.Any(a => a.Text.Contains("removed the link to the document Spec")) && card.Activity.Any(a => a.Text == "removed the end date"),
            "references and dates can be removed again; the activity log tells");
        Check(!File.ReadAllText(Path.Combine(entry.Folder, "kanban.json")).Contains("docRefs") && !File.ReadAllText(Path.Combine(entry.Folder, "kanban.json")).Contains("startDate"),
            "empty dates and references are not written to kanban.json");

        card.DocRefs = new List<string> { specPath, "Gone.md" };
        card.StartDate = Day(3, 2);
        board.Save(); board.RefreshCards();
        cardVm = todo.VisibleCards.Single();
        Check(cardVm.References.Count == 2 && cardVm.References[1].IsMissing && !cardVm.References[0].IsMissing, "a reference to a deleted document is marked as missing");

        Console.WriteLine("== v1.3: end date set automatically");
        board.MoveCardToColumn(cardVm, done);
        card = done.Data.Cards.Single();
        Check(card.EndDate != null && card.EndHasTime && (DateTime.Now - card.EndDate.Value).TotalMinutes is >= 0 and < 3 && card.Activity.Any(a => a.Text.StartsWith("end date set to")),
            "a task moved to a done column gets its end date");
        var stamped = card.EndDate;
        board.MoveCardToColumn(done.VisibleCards.Single(), todo);
        board.MoveCardToColumn(todo.VisibleCards.Single(), done);
        Check(done.Data.Cards.Single().EndDate == stamped, "an existing end date is never replaced");
        board.MoveCardToColumn(done.VisibleCards.Single(), todo);
        todo.Data.Cards.Single().EndDate = null; todo.Data.Cards.Single().EndHasTime = false;
        board.Project.Info.Settings.Roadmap.AutoEndDate = false;
        board.MoveCardToColumn(todo.VisibleCards.Single(), done);
        Check(done.Data.Cards.Single().EndDate is null, "the automatic end date can be turned off");
        board.Project.Info.Settings.Roadmap.AutoEndDate = true;
        board.MoveCardToColumn(done.VisibleCards.Single(), todo);

        Console.WriteLine("== v1.3: whiteboard references, favorites, context actions");
        main.ShowWhiteboardCommand.Execute(null);
        var wb = board.Whiteboard!;
        var rect = new WbElement { Kind = WbKind.Rectangle, Width = 100, Height = 60 };
        var note = new WbElement { Kind = WbKind.Text, Text = "Editor idea", X = 300, Width = 120, Height = 40 };
        var circle = new WbElement { Kind = WbKind.Ellipse, X = 600, Width = 80, Height = 80 };
        wb.AddElement(rect); wb.AddElement(note); wb.AddElement(circle);
        wb.Select(new[] { note.Id });
        Check(!wb.IsFavorite && wb.CanViewInGraph && !wb.CanDisconnect, "toolbar state follows the selection");
        wb.ToggleFavoriteCommand.Execute(null);
        Check(wb.IsFavorite && WhiteboardOps.Find(wb.Data, note.Id)!.Favorite && wb.CanUndo, "the star marks the selected element as favorite");
        wb.Select(new[] { note.Id, rect.Id });
        Check(!wb.IsFavorite, "a mixed selection is not shown as favorite");
        wb.ToggleFavoriteCommand.Execute(null);
        Check(wb.SelectedElements.All(e => e.Favorite), "...and the star makes all of them favorites");
        wb.Select(new[] { rect.Id });
        wb.ToggleFavoriteCommand.Execute(null);
        Check(!WhiteboardOps.Find(wb.Data, rect.Id)!.Favorite, "the star toggles");

        wb.Select(new[] { circle.Id });
        wb.ViewInGraphCommand.Execute(null);
        Check(wb.IsGraphMode && wb.GraphNodeId == circle.Id && wb.Graph.Nodes.Count == 2 && wb.Graph.Find(note.Id)!.Starred && wb.Graph.Find(rect.Id) is null,
            "View in Graph: the element becomes a node and is selected; favorites are nodes too");
        Check(wb.CanEditRefs && !wb.HasGraphRefs && wb.GraphInfo.Contains("not connected"), "the side panel of the graph lists the references of the node");
        var cardId = todo.Data.Cards.Single().Id;
        dialogs.OnDialog = vm =>
        {
            if (vm is not ItemPickerViewModel picker) return false;
            picker.Selected = picker.Items.FirstOrDefault(i => i.Id == cardId) ?? picker.Items.FirstOrDefault(i => i.Id == notesPath);
            if (picker.Selected is null) return false;
            picker.OkCommand.Execute(null);
            return true;
        };
        wb.AddCardRefCommand.Execute(null);
        wb.AddDocRefCommand.Execute(null);
        Check(wb.GraphRefs.Count == 2 && wb.GraphRefs[0] is { IsTask: true, Label: "Build the editor" } && wb.GraphRefs[1] is { IsDocument: true, Label: "Notes" } &&
              wb.Graph.Find(circle.Id)!.HasDot, "tasks and documents can be linked to the node; it gets its dot");
        Check(board.WhiteboardLinkOf(cardId) is { Label: "Ellipse 1", BoardName: "Main" }, "the task knows the whiteboard element that references it");

        wb.GraphRefs[0].OpenCommand.Execute(null);
        cardVm = todo.VisibleCards.Single();
        Check(board.IsKanbanMode && cardVm.IsHighlighted, "clicking a task reference shows the Kanban board with the task marked, not opened");
        Check(cardVm.References.Count == 3 && cardVm.References[0].IsWhiteboard && cardVm.References[0].Label == "Ellipse 1", "on the card the whiteboard element comes first");
        cardVm.References[0].OpenCommand.Execute(null);
        Check(board.IsWhiteboardMode && wb.IsBoardMode && wb.SelectedIds.SetEquals(new[] { circle.Id }), "clicking it shows the element on the whiteboard");
        board.GoToCard(cardId);
        board.ClearHighlight();
        Check(!todo.VisibleCards.Single().IsHighlighted, "the mark goes away");
        board.Filter.SearchText = "nothing matches this";
        Check(todo.VisibleCards.Count == 0 && board.GoToCard(cardId) && todo.VisibleCards.Single().IsHighlighted && board.Filter.SearchText == "", "a task hidden by a filter is shown anyway");
        Check(!board.GoToCard("missing") && dialogs.Infos.Last().Contains("no longer on the board"), "a reference to a deleted task says so");

        // one element per task, also across whiteboards
        main.ShowWhiteboardCommand.Execute(null);
        wb.Select(new[] { rect.Id });
        wb.ViewInGraphCommand.Execute(null);
        Check(wb.LinkCard(rect.Id, cardId) && WhiteboardOps.Find(wb.Data, circle.Id)!.CardRefs is null && WhiteboardOps.Find(wb.Data, rect.Id)!.CardRefs!.Single() == cardId,
            "linking the task to another element replaces the previous link");
        dialogs.PromptAnswer = "Second";
        wb.NewBoardCommand.Execute(null);
        var other = new WbElement { Kind = WbKind.Triangle, Width = 50, Height = 50 };
        wb.AddElement(other);
        Check(wb.LinkCard(other.Id, cardId) && board.WhiteboardLinkOf(cardId) is { BoardName: "Second", Label: "Triangle 1" }, "...also when the other element is on another whiteboard");
        var mainBoardInfo = wb.Boards.First(b => b.Name == "Main");
        Check(wb.Service.Load(mainBoardInfo.Id)!.Elements.All(e => e.CardRefs is null), "the old whiteboard file no longer references the task");
        Check(board.GoToWhiteboardElement(mainBoardInfo.Id, circle.Id) && wb.Data.Id == mainBoardInfo.Id && wb.SelectedIds.Contains(circle.Id), "show element switches whiteboard when needed");
        Check(!board.GoToWhiteboardElement(mainBoardInfo.Id, "nope"), "show element: a deleted element is reported");

        WhiteboardOps.AddConnector(wb.Data, rect.Id, note.Id); WhiteboardOps.AddConnector(wb.Data, rect.Id, circle.Id);
        wb.Select(new[] { rect.Id });
        Check(wb.CanDisconnect, "disconnect is available for a linked element");
        wb.DisconnectCommand.Execute(null);
        Check(wb.Data.Connectors.Count == 0 && wb.StatusText.StartsWith("2 link"), "Disconnect removes every line attached to the selection");
        wb.UndoCommand.Execute(null);
        Check(wb.Data.Connectors.Count == 2, "...and can be undone");

        Console.WriteLine("== v1.3: rich text documents");
        main.ShowDocsCommand.Execute(null);
        var dvm = board.Docs!;
        dialogs.OnDialog = vm =>
        {
            if (vm is not NewDocumentViewModel nd) return false;
            var ok = nd.IsRich && !nd.ShowTemplates;
            nd.Name = "Pitch";
            nd.CreateCommand.Execute(null);
            return ok;
        };
        dvm.NewRichDocumentCommand.Execute(null);
        var rich = dvm.ActiveTab!;
        Check(rich.Path == "Pitch.rtf" && rich.IsRich && !rich.IsText && rich.ShowRich && !rich.ShowSource && !rich.ShowFormatted && rich.IsEditable && rich.Text == DocsService.EmptyRtf,
            "New > Rich Text creates an empty .rtf document");
        var richNode = dvm.FindNode("Pitch.rtf")!;
        Check(richNode.Name == "Pitch" && richNode.IconKind == "rich" && dvm.FindNode(notesPath)!.IconKind == "markdown" && dvm.FindNode("Design")!.IconKind == "folder",
            "the tree tells folders, markdown and rich text documents apart");
        rich.Text = @"{\rtf1\ansi Hello rich \b world\b0\par}";
        Check(rich.IsDirty && rich.Save() && File.ReadAllText(docs.FullPath("Pitch.rtf")) == @"{\rtf1\ansi Hello rich \b world\b0\par}", "a rich text document is saved exactly as edited");
        rich.RefreshStats();
        Check(rich.StatsText.StartsWith("3 words") && rich.StatsText.Contains("rich text"), "word count of a rich text document");
        Check(dvm.Service.Search("rich world").Any(h => h.Path == "Pitch.rtf") && !dvm.Service.Search("rtf1").Any(h => h.Line >= 0 || h.Snippet.Contains("rtf1")), "search looks into the text of rich documents, not into the RTF codes");
        Check(dvm.Service.Index().Resolve(notesPath, "Pitch", true)?.Path == "Pitch.rtf" && dvm.Service.BuildGraph().Nodes.Any(n => n.Id == "Pitch.rtf"), "rich text documents can be the target of [[links]] and appear in the graph");

        Console.WriteLine("== v1.3: convert between markdown and rich text");
        var spec = dvm.Open(specPath)!;
        Check(spec.CanConvert && spec.ConvertText == "Convert to .RTF" && rich.ConvertText == "Convert to .MD", "the More menu offers the conversion to the other format");
        board.Data.Columns[0].Cards.Single().DocRefs = new List<string> { specPath };
        board.Save();
        File.WriteAllText(docs.FullPath(notesPath), "# Notes\n\nSee [[Spec]] and [the spec](Design/Spec.md).\n");
        dialogs.ConfirmAnswer = false;
        spec.ConvertCommand.Execute(null);
        Check(File.Exists(docs.FullPath(specPath)) && dvm.FindOpen(specPath) != null, "the conversion asks first");
        dialogs.ConfirmAnswer = true;
        spec.ConvertCommand.Execute(null);
        Check(!File.Exists(docs.FullPath(specPath)) && File.ReadAllText(docs.FullPath("Design/Spec.rtf")).StartsWith(@"{\rtf1") && dvm.ActiveTab is { Path: "Design/Spec.rtf", IsRich: true },
            "markdown to rich text: the file changes extension and content, and stays open");
        Check(Directory.GetFiles(Path.Combine(docs.Root, ".trash")).Any(f => f.EndsWith("-Spec.md")) && DocsService.HasTrash(entry.Folder), "a copy of the original goes to docs/.trash");
        Check(File.ReadAllText(docs.FullPath(notesPath)).Contains("[[Spec]]") && File.ReadAllText(docs.FullPath(notesPath)).Contains("(Design/Spec.rtf)"), "the links of the other documents follow the converted one");
        Check(board.Data.Columns[0].Cards.Single().DocRefs!.Single() == "Design/Spec.rtf", "the references of the tasks follow it too");
        dvm.ActiveTab!.ConvertCommand.Execute(null);
        Check(File.Exists(docs.FullPath(specPath)) && !File.Exists(docs.FullPath("Design/Spec.rtf")) && File.ReadAllText(docs.FullPath(specPath)).Contains("Spec") && dvm.ActiveTab is { IsText: true },
            "rich text back to markdown");
        File.WriteAllText(docs.FullPath("Design/Spec.rtf"), DocsService.EmptyRtf);
        dvm.Refresh();
        dvm.Open(specPath)!.ConvertCommand.Execute(null);
        Check(File.Exists(docs.FullPath("Design/Spec 2.rtf")) && File.Exists(docs.FullPath("Design/Spec.rtf")), "a conversion never overwrites an existing file");
        dialogs.PromptAnswer = "Requirements";
        dvm.RenameCommand.Execute(dvm.FindNode("Design/Spec 2.rtf"));
        Check(File.Exists(docs.FullPath("Design/Requirements.rtf")) && board.Data.Columns[0].Cards.Single().DocRefs!.Single() == "Design/Requirements.rtf",
            "renaming keeps the .rtf extension; references follow the rename");
        dialogs.PromptAnswer = "Specs";
        dvm.RenameCommand.Execute(dvm.FindNode("Design"));
        Check(board.Data.Columns[0].Cards.Single().DocRefs!.Single() == "Specs/Requirements.rtf", "references follow a renamed folder");

        main.ShowProjects();
        var list = (ProjectListViewModel)main.CurrentView!;
        list.Selected = list.Projects.First(p => p.Folder == entry.Folder);
        dialogs.Folders.Clear();
        list.OpenTrashCommand.Execute(null);
        Check(list.HasTrash && dialogs.Folders.Single() == DocsService.TrashPath(entry.Folder), "project list: the Trash Folder button opens docs/.trash when it has content");
        var emptyEntry = projects.Create("No Trash", "", null, "#1F6F8B", owner);
        list.Reload(emptyEntry.Folder);
        Check(!list.HasTrash, "...and is not there for a project without deleted documents");

        Console.WriteLine("== v1.3: roadmap window");
        main.OpenProject(entry);
        board = main.Board!;
        todo = board.Columns[0]; done = board.Columns[2];
        var feature = board.Data.Tags.First(t => t.Name == "Feature");
        var bug = board.Data.Tags.First(t => t.Name == "Bug");
        var first = todo.Data.Cards.Single();
        first.StartDate = Day(3, 2); first.DueDate = Day(3, 20); first.EndDate = Day(3, 25); first.EndHasTime = false; first.FinishDate = null;
        todo.Data.Cards.Add(new CardData { Title = "Fix crash", TagIds = { bug.Id, feature.Id }, StartDate = Day(3, 10), FinishDate = Day(3, 18) });
        todo.Data.Cards.Add(new CardData { Title = "No dates", TagIds = { bug.Id } });
        board.Save(); board.RefreshCards();

        dialogs.Windows.Clear();
        board.RoadmapCommand.Execute(null);
        var roadmap = board.Roadmap!;
        Check(dialogs.Windows.Single() == roadmap && roadmap.IsOpen, "the Roadmap button opens the roadmap in its own window");
        board.RoadmapCommand.Execute(null);
        Check(dialogs.Windows.Count == 1, "...only once");
        RoadmapRow Row(string key) => roadmap.Rows.First(r => r.Key == key);
        Check(roadmap.Rows.Select(r => r.Kind).Take(2).SequenceEqual(new[] { RoadmapRowKind.Milestones, RoadmapRowKind.Custom }) &&
              Row(feature.Id).Lanes.Count == 2 && Row(bug.Id).Count == 1 && Row(bug.Id).Lanes[0][0].Color == bug.Color, "one row per tag, tasks drawn with the tag color");
        var changes = 0;
        roadmap.Changed += () => changes++;
        board.MoveCardToColumn(todo.VisibleCards.First(c => c.Title == "No dates"), done);
        Check(changes > 0, "the roadmap follows the changes of the board");

        // timeline
        var clockNow = DateTime.Now;
        Check(roadmap.Months == 6 && roadmap.ViewFrom == roadmap.Timeline.From && Math.Abs(roadmap.ViewDays - (roadmap.Timeline.To - roadmap.Timeline.From).TotalDays) < 1e-9, "the timeline starts with six months");
        roadmap.Months = 3;
        Check(roadmap.Timeline.Months == 3 && new RoadmapService(entry.Folder).Load().Months == 3 && roadmap.RangeLabel.Contains("–"), "the number of months can be changed and is remembered");
        var labelBefore = roadmap.RangeLabel;
        roadmap.NextCommand.Execute(null);
        Check(roadmap.RangeLabel != labelBefore && roadmap.ViewFrom == roadmap.Timeline.From, "the arrows move the timeline");
        roadmap.TodayCommand.Execute(null);
        Check(roadmap.Timeline.Year == clockNow.Year && roadmap.ViewFrom <= clockNow && roadmap.ViewTo > clockNow, "Today brings the current month back");
        roadmap.SetHover(new DateTime(2026, 5, 2));
        Check(roadmap.HoverText == "02 May 2026 - 18w", "the date under the mouse");
        roadmap.SetHover(null);

        // free view
        roadmap.Zoom(0.5, clockNow);
        var monthsDays = roadmap.ViewDays;
        Check(!roadmap.IsFreeView, "zoom does nothing outside the free view");
        roadmap.ToggleFreeViewCommand.Execute(null);
        var pivot = roadmap.ViewFrom.AddDays(roadmap.ViewDays / 2);
        roadmap.Zoom(0.5, pivot);
        Check(roadmap.IsFreeView && Math.Abs(roadmap.ViewDays - monthsDays / 2) < 1e-6 && Math.Abs((pivot - roadmap.ViewFrom).TotalDays - roadmap.ViewDays / 2) < 1e-6, "free view: zoom keeps the date under the mouse in place");
        for (var i = 0; i < 40; i++) roadmap.Zoom(0.5, pivot);
        Check(Math.Abs(roadmap.ViewDays - 7) < 1e-9, "free view: never less than a week");
        for (var i = 0; i < 40; i++) roadmap.Zoom(2, pivot);
        Check(Math.Abs(roadmap.ViewDays - 730) < 1e-9, "free view: never more than two years");
        var from = roadmap.ViewFrom;
        roadmap.Step(1, fast: false);
        var slow = (roadmap.ViewFrom - from).TotalDays;
        roadmap.Step(-1, fast: true);
        Check(slow > 0 && (from - roadmap.ViewFrom).TotalDays > slow, "free view: arrow keys move slowly, Shift moves fast");
        var label = roadmap.RangeLabel;
        roadmap.Pan(400);
        Check(roadmap.RangeLabel == label, "the label of the months does not follow the free view");
        roadmap.ToggleFreeViewCommand.Execute(null);
        Check(!roadmap.IsFreeView && roadmap.ViewFrom == roadmap.Timeline.From, "leaving the free view goes back to the months of the label");

        // selection and actions on tasks
        var featureBar = Row(feature.Id).Lanes.SelectMany(l => l).First(b => b.Title == "Build the editor");
        roadmap.Select(featureBar);
        Check(roadmap.HasSelection && roadmap.IsSelected(featureBar) && !roadmap.CanDeleteSelection && roadmap.SelectionColors.Back == roadmap.Settings.SelectionBackLight, "a click selects a task; real tasks cannot be deleted from the roadmap");
        roadmap.DeleteSelectedCommand.Execute(null);
        Check(board.Index.Find(featureBar.Id) != null, "Del does nothing on a real task");
        var opened = false;
        dialogs.OnDialog = vm => { if (vm is CardEditorViewModel ed) { opened = ed.Title == "Build the editor"; ed.FinishDate = Day(4, 1); ed.SaveCommand.Execute(null); return true; } return false; };
        roadmap.Activate(featureBar.Id, featureBar.Kind);
        Check(opened && board.Index.Find(featureBar.Id)!.FinishDate == Day(4, 1) && Row(feature.Id).Lanes.SelectMany(l => l).First(b => b.Id == featureBar.Id).Segments.Single().Fill == BarFill.Outline,
            "a double click opens the task dialog; the roadmap shows the new dates");
        roadmap.RemoveDates(featureBar.Id);
        Check(board.Index.Find(featureBar.Id) is { StartDate: null, FinishDate: null, DueDate: not null } && Row(feature.Id).Lanes.SelectMany(l => l).All(b => b.Id != featureBar.Id) && !roadmap.HasSelection,
            "Remove Start/Finish dates takes the task out of the roadmap only");

        // rows
        roadmap.ToggleCollapsed(bug.Id);
        Check(Row(bug.Id).Collapsed && new RoadmapService(entry.Folder).Load().Collapsed.Contains(bug.Id), "the eye closes a row (remembered)");
        roadmap.ToggleCollapsed(bug.Id);
        var order = roadmap.Rows.Where(r => r.Kind == RoadmapRowKind.Tag).Select(r => r.Title).ToList();
        Check(roadmap.CanMoveRow(bug.Id, 1) && !roadmap.CanMoveRow(bug.Id, -1), "the first tag row can only go down");
        roadmap.MoveRow(bug.Id, 1);
        Check(roadmap.Rows.Where(r => r.Kind == RoadmapRowKind.Tag).Select(r => r.Title).SequenceEqual(new[] { order[1], order[0], order[2] }), "tag rows can be reordered");
        roadmap.PlaceRow(bug.Id, 2);
        Check(roadmap.Rows.Where(r => r.Kind == RoadmapRowKind.Tag).Last().Key == bug.Id, "...also by dropping a row at a position");

        Console.WriteLine("== v1.3: milestones and custom tasks");
        Action<MilestoneEditorViewModel>? fill = null;
        dialogs.OnDialog = vm =>
        {
            if (vm is MilestoneEditorViewModel me) { fill!(me); return me.Result != null || me.DeleteSelf; }
            if (vm is RoadmapItemEditorViewModel ie)
            {
                ie.Name = "Trade show"; ie.Start = Day(4, 1);
                ie.SaveCommand.Execute(null);
                var rejected = ie.Result is null && ie.HasError;
                ie.End = Day(4, 3);
                ie.SaveCommand.Execute(null);
                return rejected && ie.Result != null;
            }
            return false;
        };
        fill = me => { me.Name = ""; me.SaveCommand.Execute(null); var bad = me.HasError; me.Name = "v1.0"; me.Type = me.Types.First(t => t.Id == "major"); me.Start = Day(3, 1); me.End = Day(3, 31); me.Note = "first release"; me.SaveCommand.Execute(null); if (!bad) me.CloseRequested = null; };
        roadmap.AddMilestoneCommand.Execute(null);
        Check(roadmap.Data.Milestones.Single() is { Name: "v1.0", TypeId: "major", Note: "first release" } && Row("milestones").Lanes.Single().Single().Icon == "ion:rocket", "a milestone is created from the roadmap window");
        dialogs.Errors.Clear(); dialogs.QuietErrors = true;
        fill = me => { me.Name = "copy"; me.Type = me.Types.First(t => t.Id == "minor"); me.Start = Day(3, 1); me.End = Day(3, 31); me.SaveCommand.Execute(null); };
        roadmap.AddMilestoneCommand.Execute(null);
        Check(roadmap.Data.Milestones.Count == 1 && dialogs.Errors.Single().Contains("cannot have the same dates"), "two milestones with the same dates on the same line: an error, nothing is created");
        dialogs.QuietErrors = false;
        dialogs.ConfirmAnswer = false;
        fill = me => { me.Name = "v1.0.1"; me.Type = me.Types.First(t => t.Id == "bugfix"); me.Start = Day(3, 5); me.End = Day(3, 9); me.SaveCommand.Execute(null); };
        roadmap.AddMilestoneCommand.Execute(null);
        Check(roadmap.Data.Milestones.Count == 1, "a milestone that would be hidden: answering No goes back to the dialog, nothing is created");
        dialogs.ConfirmAnswer = true;
        roadmap.AddMilestoneCommand.Execute(null);
        Check(roadmap.Data.Milestones.Count == 1, "answering Yes: the hidden milestone is not kept");
        fill = me => { me.Name = "v2.0"; me.Type = me.Types.First(t => t.Id == "major"); me.Start = Day(2, 1); me.End = Day(4, 30); me.SaveCommand.Execute(null); };
        roadmap.AddMilestoneCommand.Execute(null);
        Check(roadmap.Data.Milestones.Single().Name == "v2.0", "a milestone that hides another one: confirmed, the hidden one is deleted");
        fill = me => { me.Name = "v2.1"; me.Start = Day(4, 20); me.End = Day(5, 20); me.Type = me.Types.First(t => t.Id == "minor"); me.SaveCommand.Execute(null); };
        roadmap.AddMilestoneCommand.Execute(null);
        var lane = Row("milestones").Lanes.Single();
        Check(lane.Count == 2 && lane[1].Segments[0].Fill == BarFill.Mixed, "overlapping milestones share the line; the shared days are mixed");
        board.Project.Info.Settings.Roadmap.MilestoneTypes.First(t => t.Id == "minor").Level = 4;
        roadmap.Refresh();
        Check(Row("milestones").Lanes.Count == 2 && Row("milestones").Lanes[1].Single().Title == "v2.1", "a type on another level gets its own line");
        var v21 = roadmap.Data.Milestones.First(m => m.Name == "v2.1");
        fill = me => { var ok = me.Name == "v2.1" && !me.IsNew; me.End = Day(6, 1); me.SaveCommand.Execute(null); if (!ok) me.CloseRequested = null; };
        roadmap.Activate(v21.Id, RoadmapBarKind.Milestone);
        Check(roadmap.Data.Milestones.First(m => m.Name == "v2.1").End == Day(6, 1), "a double click edits a milestone");

        roadmap.AddCustomCommand.Execute(null);
        var customBar = Row("custom").Lanes.Single().Single();
        Check(roadmap.Data.Items.Single().Name == "Trade show" && customBar.Segments.Single().Fill == BarFill.Outline && customBar.Color == roadmap.Settings.CustomColor,
            "a custom task needs a due or an end date; without a due date it is a plain bar");
        roadmap.Select(customBar);
        Check(roadmap.CanDeleteSelection, "custom tasks and milestones can be deleted from the roadmap");
        dialogs.ConfirmAnswer = false;
        roadmap.DeleteSelectedCommand.Execute(null);
        Check(roadmap.Data.Items.Count == 1, "deleting asks first");
        dialogs.ConfirmAnswer = true;
        roadmap.DeleteSelectedCommand.Execute(null);
        Check(roadmap.Data.Items.Count == 0 && !roadmap.HasSelection && new RoadmapService(entry.Folder).Load().Milestones.Count == 2, "Del removes the custom task; roadmap.json holds milestones and custom tasks");

        Console.WriteLine("== v1.3: roadmap settings, icons, template");
        using var zip = File.OpenRead(Path.Combine(FindRepo(), "src", "HighshoreCairn", "Assets", "ionicons.zip"));
        main.Icons = new IconLibrary(zip);
        var externalIcon = Path.Combine(Path.GetTempPath(), "hk-icon-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(externalIcon, new byte[] { 1, 2, 3 });
        dialogs.FileAnswer = externalIcon;
        var pickerSteps = 0;
        dialogs.OnDialog = vm =>
        {
            if (vm is IconPickerViewModel ip)
            {
                pickerSteps++;
                if (pickerSteps == 1)
                {
                    var ok = ip.Variant == "Filled" && ip.Selected?.Key == "ion:rocket" && ip.Items.Count == IconPickerViewModel.MaxShown && ip.Summary.StartsWith("Showing");
                    ip.Variant = "Outline"; ip.Filter = "flag";
                    ok &= ip.Items.Count is > 0 and < 10 && ip.Items.All(i => i.Name.Contains("flag") && i.Name.EndsWith("-outline"));
                    ip.Selected = ip.Items.First(i => i.Name == "flag-outline");
                    ip.OkCommand.Execute(null);
                    return ok;
                }
                ip.BrowseCommand.Execute(null);
                return ip.Result != null;
            }
            if (vm is ProjectSettingsViewModel ps)
            {
                var ok = ps.SelectedTab == 5 && ps.MilestoneTypes.Count == 4;
                ps.MilestonesName = "Releases"; ps.CustomName = "Events"; ps.CustomColor = "#123456";
                ps.ShowWeekNumber = false; ps.SelectionBackDark = "#ABCDEF";
                ps.PickMilestoneIconCommand.Execute(ps.MilestoneTypes[0]);
                ps.PickMilestoneIconCommand.Execute(ps.MilestoneTypes[1]);
                ps.AddMilestoneTypeCommand.Execute(null);
                ps.MilestoneTypes[4].Name = ""; 
                ps.SaveCommand.Execute(null);
                ok &= ps.HasError && !ps.Saved;
                ps.MilestoneTypes[4].Name = "Hotfix"; ps.MilestoneTypes[4].Level = 9;
                ps.RemoveMilestoneTypeCommand.Execute(ps.MilestoneTypes[3]);
                ps.SelectionTextLight = "red";
                ps.SaveCommand.Execute(null);
                ok &= ps.HasError;
                ps.ResetSelectionColorsCommand.Execute(null);
                ps.SelectionBackDark = "#ABCDEF";
                ps.SaveCommand.Execute(null);
                return ok && ps.Saved;
            }
            return false;
        };
        roadmap.SettingsCommand.Execute(null);
        var saved = projects.TryLoadEntry(entry.Folder)!.Info.Settings.Roadmap;
        Check(saved.MilestonesName == "Releases" && saved.CustomName == "Events" && saved.CustomColor == "#123456" && !saved.ShowWeekNumber && saved.SelectionBackDark == "#ABCDEF",
            "Settings > Roadmap: row names, colors, week number, selection colors");
        Check(saved.MilestoneTypes.Count == 4 && saved.MilestoneTypes[0].Icon == "ion:flag-outline" && saved.MilestoneTypes[1].Icon.StartsWith("file:hk-icon-") &&
              saved.MilestoneTypes[3] is { Name: "Hotfix", Level: 5 } && saved.MilestoneTypes.All(t => t.Id != "beta"),
            "milestone types can be added, removed and changed (icon, level 1-5)");
        var iconFile = IconLibrary.FileName(saved.MilestoneTypes[1].Icon)!;
        Check(File.Exists(Path.Combine(entry.Folder, "roadmap-icons", iconFile)), "an external icon is copied into the project");
        Check(roadmap.Rows[0].Title == "Releases" && roadmap.Rows[1].Title == "Events" && roadmap.AddMilestoneText == "+ Release" && roadmap.AddCustomText == "+ Events task", "the roadmap window follows the settings");
        roadmap.SetHover(new DateTime(2026, 5, 2));
        Check(roadmap.HoverText == "02 May 2026", "the week number can be hidden");

        dialogs.OnDialog = vm => { if (vm is ProjectSettingsViewModel ps) { ps.SetTemplateCommand.Execute(null); ps.CancelCommand.Execute(null); return true; } return false; };
        board.OpenSettings();
        var template = projects.LoadTemplate()!;
        Check(template.Settings.Roadmap.MilestonesName == "Releases" && template.Settings.Roadmap.MilestoneTypes.Count == 4 && File.Exists(Path.Combine(ProjectService.TemplateIconsFolder, iconFile)),
            "the default template takes the roadmap settings and a copy of the custom icons");
        var fromTemplate = projects.Create("From Template", "", null, "#1F6F8B", owner);
        Check(fromTemplate.Info.Settings.Roadmap.CustomName == "Events" && File.Exists(Path.Combine(fromTemplate.Folder, "roadmap-icons", iconFile)) &&
              !File.Exists(Path.Combine(fromTemplate.Folder, "roadmap.json")), "a new project starts with those settings and icons, without milestones or tasks");
        projects.ClearTemplate();
        Check(!Directory.Exists(ProjectService.TemplateIconsFolder) && projects.LoadTemplate() is null, "restoring the original template removes the copied icons");

        main.ShowProjects();
        Check(!roadmap.IsOpen && dialogs.Windows.Count == 0, "closing the project closes its roadmap window");
        projects.Delete(fromTemplate.Folder);
        projects.Delete(emptyEntry.Folder);
        projects.Delete(entry.Folder);
        Check(!File.Exists(Path.Combine(entry.Folder, "roadmap.json")) && !Directory.Exists(Path.Combine(entry.Folder, "roadmap-icons")) && Directory.Exists(Path.Combine(entry.Folder, "docs")),
            "deleting a project removes roadmap.json and the icons, never the documents");
        try { File.Delete(externalIcon); } catch { }
        dialogs.OnDialog = null; dialogs.FileAnswer = null;
    }

    /// <summary>Scenarios found by the review of v1.3: links that must follow what happens in the other areas.</summary>
    public static void Review(Action<bool, string> Check, MainViewModel main, ProjectService projects, FakeDialogs dialogs, Account owner)
    {
        Console.WriteLine("== v1.3 review: references stay consistent");
        dialogs.OnDialog = null; dialogs.ConfirmAnswer = true; dialogs.Infos.Clear();
        main.DocConverter = new FakeConverter();
        var entry = projects.Create("Links Project", "", null, "#1F6F8B", owner);
        var docs = new DocsService(entry.Folder);
        var specPath = docs.CreateDocument("Design", "Spec", "# Spec\n");
        var notesPath = docs.CreateDocument("", "Notes", "# Notes\n");
        main.OpenProject(entry);
        var board = main.Board!;
        var todo = board.Columns[0]; var doing = board.Columns[1];
        var cardX = new CardData { Title = "Task X", DocRefs = new List<string> { specPath, notesPath } };
        var cardY = new CardData { Title = "Task Y" };
        var cardZ = new CardData { Title = "Task Z" };
        todo.Data.Cards.Add(cardX); todo.Data.Cards.Add(cardY); doing.Data.Cards.Add(cardZ);
        board.Save(); board.RefreshCards();

        main.ShowWhiteboardCommand.Execute(null);
        var wb = board.Whiteboard!;
        var a = new WbElement { Kind = WbKind.Text, Text = "Alpha", Width = 80, Height = 30 };
        var b = new WbElement { Kind = WbKind.Rectangle, X = 200, Width = 80, Height = 60 };
        wb.AddElement(b); wb.AddElement(a);
        WbElement El(string id) => WhiteboardOps.Find(wb.Data, id)!;

        // links are not undone
        var undoBefore = wb.CanUndo;
        wb.LinkCard(a.Id, cardX.Id);
        wb.LinkDoc(a.Id, specPath);
        wb.Select(new[] { a.Id });
        wb.ToggleFavoriteCommand.Execute(null);
        wb.UndoCommand.Execute(null);
        Check(!El(a.Id).Favorite && El(a.Id).CardRefs!.Single() == cardX.Id && El(a.Id).DocRefs!.Single() == specPath, "undo takes back the star, not the references");
        wb.RedoCommand.Execute(null);
        Check(El(a.Id).Favorite && El(a.Id).CardRefs!.Single() == cardX.Id, "...and redo keeps them too");

        // merge + split
        wb.Select(new[] { a.Id, b.Id });
        wb.GroupCommand.Execute(null);
        var groupId = wb.SelectedIds.Single();
        Check(board.WhiteboardLinkOf(cardX.Id)?.ElementId == groupId, "merged: the task points to the whole");
        wb.UngroupCommand.Execute(null);
        Check(El(a.Id).CardRefs!.Single() == cardX.Id && El(a.Id).Favorite && El(b.Id).CardRefs is null && !El(b.Id).Favorite && board.WhiteboardLinkOf(cardX.Id)?.Label == "Alpha",
            "split: the link and the star are back on the element that had them");

        // a renamed document, then undo
        main.ShowDocsCommand.Execute(null);
        var dvm = board.Docs!;
        dialogs.PromptAnswer = "Requirements";
        dvm.RenameCommand.Execute(dvm.FindNode(specPath));
        var renamed = "Design/Requirements.md";
        main.ShowWhiteboardCommand.Execute(null);
        Check(El(a.Id).DocRefs!.Single() == renamed && cardX.DocRefs!.Contains(renamed), "a renamed document is followed by tasks and elements");
        wb.UndoCommand.Execute(null);   // back before the split
        wb.UndoCommand.Execute(null);   // back before the merge
        Check(El(a.Id).DocRefs!.Single() == renamed && wb.Service.Load(wb.Data.Id)!.Elements.SelectMany(e => e.DocRefs ?? new List<string>()).All(d => d == renamed),
            "undo after the rename does not bring the old name back");

        // an element deleted and brought back
        wb.Select(new[] { a.Id });
        wb.DeleteCommand.Execute(null);
        Check(board.WhiteboardLinkOf(cardX.Id) is null, "deleting the element removes the link from the task");
        wb.UndoCommand.Execute(null);
        Check(El(a.Id).CardRefs!.Single() == cardX.Id && board.WhiteboardLinkOf(cardX.Id)?.ElementId == a.Id, "undoing the delete brings the element back with its task");
        wb.Select(new[] { a.Id });
        wb.DeleteCommand.Execute(null);
        wb.LinkCard(b.Id, cardX.Id);
        wb.UndoCommand.Execute(null);
        Check(El(a.Id).CardRefs is null && El(b.Id).CardRefs!.Single() == cardX.Id, "...unless the task was linked to another element in the meantime");

        // cut and paste to another whiteboard
        wb.Select(new[] { b.Id });
        var clip = wb.CopySelection()!;
        wb.DeleteCommand.Execute(null);
        dialogs.PromptAnswer = "Second";
        wb.NewBoardCommand.Execute(null);
        Check(wb.Paste(clip, new WbPoint(50, 50)) && wb.Data.Elements.Single().CardRefs!.Single() == cardX.Id && board.WhiteboardLinkOf(cardX.Id)?.BoardName == "Second",
            "an element cut and pasted on another whiteboard keeps its task");
        Check(wb.Paste(clip, new WbPoint(90, 90)) && wb.Data.Elements.Count == 2 && wb.Data.Elements[1].CardRefs is null, "...a second paste is a copy: no task");
        var pasted = wb.Data.Elements[0];

        // a task that cannot be seen because its column is filtered out
        wb.LinkCard(pasted.Id, cardZ.Id);
        main.ShowKanbanCommand.Execute(null);
        board.Filter.Columns.First(c => c.Id == todo.Data.Id).IsSelected = true;
        board.RefreshCards();
        Check(!doing.IsVisible && board.GoToCard(cardZ.Id) && doing.IsVisible && doing.VisibleCards.Single().IsHighlighted, "go to a task in a column hidden by the filter: the filter is cleared");

        // deleting things
        main.ShowWhiteboardCommand.Execute(null);
        wb.Select(new[] { pasted.Id });
        wb.ViewInGraphCommand.Execute(null);
        Check(wb.GraphRefs.Count == 2 && wb.Graph.Find(pasted.Id)!.HasDot, "the graph panel lists both tasks");
        main.ShowKanbanCommand.Execute(null);
        cardX.Title = "Task X renamed";
        board.DeleteCard(doing.VisibleCards.Single());
        Check(wb.Data.Elements[0].CardRefs!.Single() == cardX.Id && wb.Service.Load(wb.Data.Id)!.Elements[0].CardRefs!.Single() == cardX.Id, "a deleted task is forgotten by its element (also in the file)");
        main.ShowWhiteboardCommand.Execute(null);
        Check(wb.GraphRefs.Single().Label == "Task X renamed", "back on the whiteboard the panel shows the tasks as they are now");
        board.ArchiveCard(board.Columns[0].VisibleCards.First(c => c.Data == cardX));
        main.ShowKanbanCommand.Execute(null); main.ShowWhiteboardCommand.Execute(null);
        Check(wb.Data.Elements[0].CardRefs!.Single() == cardX.Id && wb.GraphRefs.Single() is { IsMissing: true } archivedRef && archivedRef.Label.Contains("archived"), "an archived task keeps its link and is shown as archived");
        board.DeleteArchivedCard(cardX);
        Check(wb.Data.Elements[0].CardRefs is null && !wb.Graph.Find(pasted.Id)!.HasDot, "deleting it from the archive removes the link and the dot");

        wb.LinkDoc(pasted.Id, notesPath);
        wb.LinkDoc(pasted.Id, renamed);
        todo.Data.Cards.Single().DocRefs = new List<string> { notesPath, renamed };
        board.Save();
        main.ShowDocsCommand.Execute(null);
        dvm.DeleteCommand.Execute(dvm.FindNode("Design"));
        Check(todo.Data.Cards.Single().DocRefs!.SequenceEqual(new[] { notesPath }) && wb.Data.Elements[0].DocRefs!.SequenceEqual(new[] { notesPath }) &&
              projects.LoadBoard(entry.Folder).Columns[0].Cards.Single().DocRefs!.Count == 1, "a deleted folder: tasks and elements forget its documents");
        dvm.DeleteCommand.Execute(dvm.FindNode(notesPath));
        var mainInfo = wb.Boards.First(i => i.Name == "Main");
        Check(todo.Data.Cards.Single().DocRefs is null && wb.Data.Elements[0].DocRefs is null && !File.ReadAllText(Path.Combine(entry.Folder, "kanban.json")).Contains("docRefs") &&
              wb.Service.Load(mainInfo.Id)!.Elements.All(e => e.DocRefs is null), "a deleted document: no reference is left anywhere");

        // an open rich text document changed by another program
        var richPath = docs.CreateRichDocument("", "Pitch");
        dvm.Refresh();
        var rich = dvm.Open(richPath)!;
        var external = @"{\rtf1\ansi Edited in WordPad\par}";
        File.WriteAllText(docs.FullPath(richPath), external);
        File.SetLastWriteTimeUtc(docs.FullPath(richPath), DateTime.UtcNow.AddSeconds(5));
        dvm.CheckExternalChanges();
        Check(rich.Text == external, "a rich text document changed by another program is reloaded, not overwritten");
        dvm.IsGraphVisible = true;
        dvm.SelectGraphNode(richPath);
        Check(dvm.GraphMarkdown.Contains("Edited in WordPad") && !dvm.GraphMarkdown.Contains("rtf1"), "the graph preview of a rich text document shows its text");

        Console.WriteLine("== v1.3 review: roadmap");
        main.ShowKanbanCommand.Execute(null);
        var service = new RoadmapService(entry.Folder);
        DateTime Day(int d) => new(2026, 5, d);
        service.Save(new RoadmapData { Milestones =
        {
            new Milestone { Id = "m1", Name = "One", TypeId = "major", Start = Day(1), End = Day(20) },
            new Milestone { Id = "m2", Name = "Two", TypeId = "beta", Start = Day(5), End = Day(10) }
        } });
        board.Project.Info.Settings.Roadmap.MilestoneTypes.First(t => t.Id == "beta").Level = 2;
        board.RoadmapCommand.Execute(null);
        var roadmap = board.Roadmap!;
        Check(roadmap.Rows[0].Lanes.Count == 2, "two levels, two lines");
        var asked = 0;
        dialogs.OnDialog = vm =>
        {
            if (vm is not ProjectSettingsViewModel ps) return false;
            asked++;
            if (asked > 1) { ps.CancelCommand.Execute(null); return true; }
            ps.MilestoneTypes.First(t => t.Id == "beta").Level = 1;
            ps.SaveCommand.Execute(null);
            return true;
        };
        dialogs.ConfirmAnswer = false;
        board.OpenSettings("Roadmap");
        Check(asked == 2 && roadmap.Data.Milestones.Count == 2 && roadmap.Rows[0].Lanes.Count == 1, "a level change that hides a milestone warns; No goes back to the settings");
        dialogs.ConfirmAnswer = true;
        asked = -5;
        dialogs.OnDialog = vm => { if (vm is not ProjectSettingsViewModel ps) return false; asked++; ps.SaveCommand.Execute(null); return true; };
        board.OpenSettings("Roadmap");
        Check(asked == -4 && roadmap.Data.Milestones.Single().Id == "m1" && service.Load().Milestones.Count == 1 && roadmap.Rows[0].Lanes.Count == 1, "Yes deletes the hidden milestone");
        dialogs.OnDialog = null;

        var label = roadmap.RangeLabel;
        roadmap.ToggleFreeViewCommand.Execute(null);
        roadmap.NextCommand.Execute(null); roadmap.PreviousCommand.Execute(null); roadmap.NextCommand.Execute(null);
        roadmap.Pan(900);
        roadmap.TodayCommand.Execute(null);
        Check(roadmap.RangeLabel == label && roadmap.ViewFrom <= roadmap.Now && roadmap.ViewTo > roadmap.Now, "free view: the arrows do nothing, Today moves the view, the label stays");
        for (var i = 0; i < 3000; i++) roadmap.Pan(-200000);
        Check(roadmap.ViewFrom.Year >= 3 && roadmap.ViewTo > roadmap.ViewFrom, "free view: panning stops before the dates run out");
        roadmap.ToggleFreeViewCommand.Execute(null);

        main.ShowProjects();
        File.WriteAllText(service.File, "<<<<<<< HEAD\n{ broken");
        main.OpenProject(entry);
        board = main.Board!;
        dialogs.QuietErrors = true; dialogs.Errors.Clear();
        board.RoadmapCommand.Execute(null);
        roadmap = board.Roadmap!;
        roadmap.Months = roadmap.Months == 4 ? 5 : 4;   // any change saves
        dialogs.QuietErrors = false;
        var kept = Directory.GetFiles(entry.Folder, "roadmap.json.unreadable-*");
        Check(dialogs.Errors.Count == 1 && kept.Length == 1 && File.ReadAllText(kept[0]).StartsWith("<<<<<<<") && service.Load().Milestones.Count == 0,
            "an unreadable roadmap file is kept under another name before a new one is written");

        main.ShowProjects();
        projects.Delete(entry.Folder);
        dialogs.OnDialog = null; dialogs.PromptAnswer = "";
    }

    static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "HighshoreCairn.sln"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
