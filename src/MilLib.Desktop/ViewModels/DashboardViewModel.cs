using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>
/// The operating picture for the day, drawn rather than tabulated.
///
/// The same questions a librarian opens the application to answer — how much of
/// the collection is out, how much is on the shelf, what is late, how busy the
/// week has been, what has just been catalogued — but shown as rings, a donut
/// and a bar chart, so the shape of the day is read before any number is. The
/// one list kept is the overdue one, because it is the only thing here that
/// asks somebody to act today.
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    private readonly Action<string> _go;

    [ObservableProperty] private bool _busy = true;
    [ObservableProperty] private string _problem = "";

    [ObservableProperty] private int _titles;
    [ObservableProperty] private int _copies;
    [ObservableProperty] private int _availableCopies;
    [ObservableProperty] private int _issued;
    [ObservableProperty] private int _overdue;
    [ObservableProperty] private int _members;
    [ObservableProperty] private int _issuedToday;
    [ObservableProperty] private int _issuedThisWeek;

    [ObservableProperty] private string _greeting = "";

    /// <summary>Who is signed in and to what — the same line the web console carries.</summary>
    [ObservableProperty] private string _standing = "";

    public DashboardViewModel(Action<string>? go = null)
    {
        _go = go ?? (_ => { });

        Greeting = Welcome();
        Standing = WhoAndWhen();

        _ = LoadAsync();
    }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>The three rings: what is out, what is in, and how much is late.</summary>
    public ObservableCollection<Gauge> Gauges { get; } = [];

    /// <summary>The collection split by state, as the wedges of a donut.</summary>
    public ObservableCollection<Wedge> Collection { get; } = [];

    /// <summary>The last seven days of issues, as bars.</summary>
    public ObservableCollection<Bar> Week { get; } = [];

    /// <summary>
    /// The most recently catalogued books that actually have a cover — the strip
    /// that scrolls across the top of the dashboard. Only covered books, because
    /// a shelf of grey placeholders is not what makes a library look like one.
    /// </summary>
    public ObservableCollection<RecentBook> Covers { get; } = [];

    /// <summary>Raised when the cover strip has been refilled, so the view can
    /// restart the scroll against the new width.</summary>
    public event Action? CoversChanged;

    /// <summary>
    /// The small figures a librarian acts on today — what is due back, what is
    /// waiting to be collected, what is owed, whose card is about to lapse, and
    /// what has gone astray. Built from what this person may see and what the
    /// unit has switched on, so a counter with no reservations gets a shorter
    /// row rather than one with a hole in it.
    /// </summary>
    public ObservableCollection<Tile> Tiles { get; } = [];

    /// <summary>
    /// The holdings by security marking — the view a military library is asked
    /// for that an ordinary one never is. Each marking in its conventional
    /// colour, so the shape of the classified holding is read at a glance.
    /// </summary>
    public ObservableCollection<Segment> Classified { get; } = [];

    /// <summary>The most-borrowed titles — what the unit actually reads.</summary>
    public ObservableCollection<PopularBook> Popular { get; } = [];

    public bool HasPopular => Popular.Count > 0;

    public bool HasClassified => Classified.Count > 0;

    public List<OverdueRow> Overdues { get; } = [];

    public bool NothingOverdue => !Busy && Overdues.Count == 0;

    /// <summary>The headline counts, spelt out under the rings.</summary>
    public string TitlesText => $"{Titles:N0}";

    public string CopiesText => $"{Copies:N0}";

    public string MembersText => $"{Members:N0}";

    public string WeekText => IssuedThisWeek == 1 ? "1 issue in the last 7 days"
        : $"{IssuedThisWeek:N0} issues in the last 7 days";

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    [RelayCommand]
    private void Go(string section) => _go(section);

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Busy = true;
        Problem = "";

        try
        {
            await using var db = Workspace.Open();

            var today = DateOnly.FromDateTime(DateTime.Today);

            Titles = await db.Titles.CountAsync();
            Copies = await db.Copies.CountAsync();
            AvailableCopies = await db.Copies.CountAsync(c => c.Status == CopyStatus.AVAILABLE);
            Members = await db.Members.CountAsync(m => m.Status == MemberStatus.ACTIVE);

            // Read off the loans rather than off copy.status. The two agree
            // almost always, and when they don't it is the loan that is right.
            var open = db.Loans.Where(l => l.Status != LoanStatus.RETURNED);

            Issued = await open.CountAsync();
            Overdue = await open.CountAsync(l => l.DueOn < today);
            IssuedToday = await db.Loans.CountAsync(l => l.IssuedOn >= DateTime.Today);

            var weekStart = DateTime.Today.AddDays(-6);

            var recentIssues = await db.Loans
                .Where(l => l.IssuedOn >= weekStart)
                .Select(l => l.IssuedOn)
                .ToListAsync();

            IssuedThisWeek = recentIssues.Count;

            // --- the figures the counter acts on today ------------------------
            var dueToday = await open.CountAsync(l => l.DueOn == today);
            var lostMissing = await db.Copies.CountAsync(c =>
                c.Status == CopyStatus.LOST || c.Status == CopyStatus.MISSING);
            var expiringSoon = await db.Members.CountAsync(m =>
                m.Status == MemberStatus.ACTIVE
                && m.ValidUpto != null
                && m.ValidUpto >= today
                && m.ValidUpto <= today.AddDays(30));

            var holdsReady = 0;
            var holdsWaiting = 0;
            if (Session.Has(Feature.Reservations))
            {
                holdsReady = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.READY);
                holdsWaiting = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.WAITING);
            }

            decimal pendingFines = 0;
            if (Session.Has(Feature.Fines))
            {
                pendingFines = await db.Fines
                    .Where(f => f.Status == FineStatus.PENDING)
                    .SumAsync(f => (decimal?)f.Amount) ?? 0m;
            }

            BuildTiles(dueToday, holdsReady, holdsWaiting, pendingFines, expiringSoon, lostMissing);

            // --- holdings by security marking (a military library's view) -----
            var byClass = await db.Copies
                .GroupBy(c => c.Title!.SecurityClass)
                .Select(g => new { Class = g.Key, Count = g.Count() })
                .ToListAsync();

            BuildClassified(byClass.ToDictionary(x => x.Class, x => x.Count));

            // --- the most-borrowed titles -------------------------------------
            var counts = await db.Loans
                .Where(l => l.Copy != null)
                .GroupBy(l => l.Copy!.TitleId)
                .Select(g => new { TitleId = g.Key, Loans = g.Count() })
                .OrderByDescending(x => x.Loans)
                .Take(6)
                .ToListAsync();

            var wantedIds = counts.Select(c => c.TitleId).ToList();

            var named = await db.Titles
                .Where(t => wantedIds.Contains(t.TitleId))
                .Select(t => new
                {
                    t.TitleId,
                    t.Name,
                    Author = t.Authors.OrderBy(a => a.SortOrder).Select(a => a.Author!.Name).FirstOrDefault(),
                })
                .ToListAsync();

            Popular.Clear();
            var rank = 0;
            foreach (var c in counts)
            {
                var title = named.FirstOrDefault(n => n.TitleId == c.TitleId);
                if (title is null || c.Loans == 0)
                {
                    continue;
                }

                Popular.Add(new PopularBook(++rank, title.Name, title.Author ?? "", c.Loans));
            }

            OnPropertyChanged(nameof(HasPopular));
            OnPropertyChanged(nameof(HasClassified));

            BuildGauges();
            BuildCollection();
            BuildWeek(recentIssues);

            Covers.Clear();

            var covered = await db.Titles
                .Where(t => t.CoverPath != null && t.CoverPath != "")
                .OrderByDescending(t => t.TitleId)
                .Take(24)
                .Select(t => new
                {
                    t.Name,
                    t.CoverPath,
                    Author = t.Authors.OrderBy(a => a.SortOrder).Select(a => a.Author!.Name).FirstOrDefault(),
                })
                .ToListAsync();

            foreach (var r in covered)
            {
                var file = Workspace.CoverPath(r.CoverPath);

                // Only the ones whose file is actually here — a recorded path
                // whose picture is missing would be a gap in the moving strip.
                if (file is not null)
                {
                    Covers.Add(new RecentBook(r.Name, r.Author ?? "", file));
                }
            }

            CoversChanged?.Invoke();

            Overdues.Clear();

            var rows = await open
                .Where(l => l.DueOn < today)
                .OrderBy(l => l.DueOn)
                .Take(10)
                .Select(l => new
                {
                    l.DueOn,
                    Member = l.Member!.FullName,
                    l.Member!.Rank,
                    Book = l.Copy!.Title!.Name,
                    l.Copy!.AccessionNo,
                })
                .ToListAsync();

            foreach (var row in rows)
            {
                Overdues.Add(new OverdueRow(
                    string.IsNullOrWhiteSpace(row.Rank) ? row.Member : $"{row.Rank} {row.Member}",
                    row.Book,
                    Session.Preferences.Accession(row.AccessionNo),
                    row.DueOn,
                    today.DayNumber - row.DueOn.DayNumber));
            }

            OnPropertyChanged(nameof(Overdues));
            OnPropertyChanged(nameof(TitlesText));
            OnPropertyChanged(nameof(CopiesText));
            OnPropertyChanged(nameof(MembersText));
            OnPropertyChanged(nameof(WeekText));
        }
        catch (Exception ex)
        {
            Faults.Record("reading the dashboard", ex);

            Problem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;

            OnPropertyChanged(nameof(NothingOverdue));
        }
    }

    /// <summary>
    /// The counter's figures for today, each tinted for what it is: the ordinary
    /// ones cool, the ones that are somebody's job now warm, and the one that
    /// means trouble red. Only the ones this person may see and the unit has on.
    /// </summary>
    private void BuildTiles(int dueToday, int holdsReady, int holdsWaiting,
        decimal pendingFines, int expiringSoon, int lostMissing)
    {
        var money = Session.Preferences;

        Tiles.Clear();

        if (Session.Can(Ability.CirculationOperate))
        {
            Tiles.Add(new Tile("DUE BACK TODAY", $"{dueToday:N0}",
                "over the counter today", dueToday > 0 ? "info" : "cool"));
        }

        if (Session.Has(Feature.Reservations) && Session.Can(Ability.ReservationsManage))
        {
            Tiles.Add(new Tile("HOLDS READY", $"{holdsReady:N0}",
                holdsWaiting == 1 ? "1 more in the queue" : $"{holdsWaiting} more in the queue",
                holdsReady > 0 ? "warn" : "cool"));
        }

        if (Session.Has(Feature.Fines) && Session.Can(Ability.FinesManage))
        {
            Tiles.Add(new Tile("UNPAID FINES", money.Money(pendingFines),
                "to settle at the counter", pendingFines > 0 ? "bad" : "cool"));
        }

        if (Session.Can(Ability.MembersView))
        {
            Tiles.Add(new Tile("CARDS EXPIRING", $"{expiringSoon:N0}",
                "in the next 30 days", expiringSoon > 0 ? "warn" : "cool"));
        }

        Tiles.Add(new Tile("LOST OR MISSING", $"{lostMissing:N0}",
            "copies to trace or condemn", lostMissing > 0 ? "bad" : "cool"));
    }

    /// <summary>
    /// The holdings by marking, in the order and the colours a marking is
    /// conventionally drawn in, each with a bar scaled to the largest so the
    /// classified share reads against the whole.
    /// </summary>
    private void BuildClassified(IReadOnlyDictionary<SecurityClass, int> counts)
    {
        Classified.Clear();

        SecurityClass[] order =
        [
            SecurityClass.UNCLASSIFIED, SecurityClass.RESTRICTED, SecurityClass.CONFIDENTIAL,
            SecurityClass.SECRET, SecurityClass.TOP_SECRET,
        ];

        var max = Math.Max(1, order.Select(c => counts.GetValueOrDefault(c)).DefaultIfEmpty(0).Max());

        foreach (var cls in order)
        {
            var count = counts.GetValueOrDefault(cls);

            if (count == 0)
            {
                continue;
            }

            Classified.Add(new Segment(cls, Words.Of(cls), count, Math.Max(6, 188.0 * count / max)));
        }
    }

    /// <summary>
    /// The three rings. Each is a fraction the eye can read before the number —
    /// how much of the stock is out, how much is on the shelf, and what share of
    /// the loans are overdue — with the count spelt out beneath it.
    /// </summary>
    private void BuildGauges()
    {
        Gauges.Clear();

        var outRate = Copies > 0 ? (double)Issued / Copies : 0;
        var inRate = Copies > 0 ? (double)AvailableCopies / Copies : 0;
        var lateRate = Issued > 0 ? (double)Overdue / Issued : 0;

        Gauges.Add(new Gauge("IN CIRCULATION", outRate, "Accent",
            $"{Issued:N0} of {Copies:N0}", "out with a member"));
        Gauges.Add(new Gauge("ON THE SHELF", inRate, "Good",
            $"{AvailableCopies:N0} of {Copies:N0}", "available to lend"));
        Gauges.Add(new Gauge("OVERDUE", lateRate, Overdue > 0 ? "Bad" : "Good",
            $"{Overdue:N0} of {Issued:N0}", "past the loan period"));
    }

    /// <summary>
    /// The whole collection as one donut: on the shelf, out on loan, overdue, and
    /// everything else a copy can be — reserved, in transit, lost, withdrawn.
    /// The wedges are worked out here so the drawing only has to place them.
    /// </summary>
    private void BuildCollection()
    {
        Collection.Clear();

        var onLoanOk = Math.Max(0, Issued - Overdue);
        var other = Math.Max(0, Copies - AvailableCopies - Issued);

        var parts = new (string Label, int Count, string Colour)[]
        {
            ("On the shelf", AvailableCopies, "Good"),
            ("Out on loan", onLoanOk, "Accent"),
            ("Overdue", Overdue, "Bad"),
            ("Other", other, "InkFaint"),
        };

        var total = Math.Max(1, parts.Sum(p => p.Count));
        var start = 0.0;

        foreach (var part in parts)
        {
            if (part.Count == 0)
            {
                continue;
            }

            var fraction = (double)part.Count / total;

            Collection.Add(new Wedge(part.Label, part.Count, part.Colour, start, fraction));

            start += fraction;
        }
    }

    /// <summary>
    /// The last seven days of issues as bars, tallest normalised to the busiest
    /// day so a quiet week still shows its shape. Today's bar takes the accent.
    /// </summary>
    private void BuildWeek(List<DateTime> issues)
    {
        Week.Clear();

        var byDay = new int[7];

        foreach (var when in issues)
        {
            var index = (when.Date - DateTime.Today.AddDays(-6)).Days;

            if (index is >= 0 and < 7)
            {
                byDay[index]++;
            }
        }

        var peak = Math.Max(1, byDay.Max());

        for (var i = 0; i < 7; i++)
        {
            var day = DateTime.Today.AddDays(-6 + i);

            Week.Add(new Bar(
                day.ToString("ddd").ToUpperInvariant(),
                byDay[i],
                Bar.MaxHeight * byDay[i] / peak,
                i == 6));
        }
    }

    /// <summary>Role, clearance and the date — the console's context line.</summary>
    private static string WhoAndWhen()
    {
        var role = Session.User is { } u ? Words.Any(u.Role) : "";
        var cleared = Words.Of(Session.User?.ClearanceLevel ?? SecurityClass.UNCLASSIFIED);

        return $"{role}  ·  cleared to {cleared}  ·  {DateTime.Now:dddd, dd MMMM yyyy}";
    }

    /// <summary>
    /// The time of day, said once. Not a personality — a small acknowledgement
    /// that a person opened this, at an hour, to do a job.
    /// </summary>
    private static string Welcome()
    {
        var hour = DateTime.Now.Hour;

        var part = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";

        return $"{part}, {Session.Name}";
    }
}

/// <summary>
/// The arc geometry for the rings and the donut wedges.
///
/// Drawn as a real path — start at twelve o'clock, sweep clockwise by the
/// fraction — rather than a dash on a circle, because a bound dash array did not
/// take on the stroke. The path is worked out once, in the model, so the drawing
/// only has to stroke it.
/// </summary>
internal static class Arc
{
    public static Geometry Ring(double diameter, double thickness, double startFraction, double lengthFraction)
    {
        var r = (diameter - thickness) / 2;
        var c = diameter / 2;

        // A full turn is drawn as a circle — an arc from a point back to itself
        // is degenerate and vanishes.
        if (lengthFraction >= 0.999)
        {
            return new EllipseGeometry(new Rect(c - r, c - r, r * 2, r * 2));
        }

        if (lengthFraction <= 0.0001)
        {
            return new StreamGeometry();
        }

        double Angle(double f) => (f * 360 - 90) * Math.PI / 180;

        var a0 = Angle(startFraction);
        var a1 = Angle(startFraction + lengthFraction);

        var start = new Point(c + (r * Math.Cos(a0)), c + (r * Math.Sin(a0)));
        var end = new Point(c + (r * Math.Cos(a1)), c + (r * Math.Sin(a1)));

        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false);
            ctx.ArcTo(end, new Size(r, r), 0, lengthFraction > 0.5, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }

        return geometry;
    }
}

/// <summary>
/// One ring on the dashboard: a fraction shown as an arc, the percentage in the
/// middle and a count beneath.
/// </summary>
public sealed class Gauge
{
    /// <summary>The ring's fixed size, shared with the drawing so the maths agrees.</summary>
    public const double Diameter = 132;

    public const double Thickness = 12;

    public Gauge(string label, double fraction, string colour, string count, string note)
    {
        Label = label;
        Fraction = Math.Clamp(fraction, 0, 1);
        Colour = colour;
        Count = count;
        Note = note;
        Percent = $"{Math.Round(Fraction * 100)}%";

        Track = Arc.Ring(Diameter, Thickness, 0, 1);
        Value = Arc.Ring(Diameter, Thickness, 0, Fraction);
    }

    public string Label { get; }

    public double Fraction { get; }

    public string Percent { get; }

    public string Count { get; }

    public string Note { get; }

    /// <summary>The palette key the arc is drawn in — Accent, Good, Bad.</summary>
    public string Colour { get; }

    /// <summary>The full faint ring behind, and the lit arc over it.</summary>
    public Geometry Track { get; }

    public Geometry Value { get; }
}

/// <summary>One wedge of the collection donut, from where the last one ended.</summary>
public sealed class Wedge
{
    public const double Diameter = 168;

    public const double Thickness = 22;

    public Wedge(string label, int count, string colour, double start, double fraction)
    {
        Label = label;
        Count = count;
        Colour = colour;

        // A hair of a gap between wedges so they read as separate slices.
        var gap = fraction > 0.02 ? 0.006 : 0;

        Value = Arc.Ring(Diameter, Thickness, start, Math.Max(0, fraction - gap));
    }

    public string Label { get; }

    public int Count { get; }

    public string Colour { get; }

    public Geometry Value { get; }
}

/// <summary>One day's issues, as a bar.</summary>
public sealed class Bar(string day, int count, double height, bool today)
{
    public const double MaxHeight = 96;

    public string Day { get; } = day;

    public int Count { get; } = count;

    /// <summary>The bar's height in pixels, the busiest day filling the chart.</summary>
    public double Height { get; } = Math.Max(count > 0 ? 4 : 2, height);

    public bool IsToday { get; } = today;

    public string CountText => count.ToString();
}

/// <summary>A recently catalogued book, with its cover loaded on demand.</summary>
public sealed class RecentBook(string title, string author, string? coverFile)
{
    private Bitmap? _cover;
    private bool _loaded;

    public string Title { get; } = title;

    public string Author { get; } = author;

    public Bitmap? Cover
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                _cover = Pictures.Load(coverFile);
            }

            return _cover;
        }
    }

    public bool HasCover => Cover is not null;
}

/// <summary>One small figure on the dashboard, tinted for what kind it is.</summary>
public sealed record Tile(string Label, string Value, string Note, string Tone)
{
    public bool IsCool => Tone == "cool";

    public bool IsInfo => Tone == "info";

    public bool IsWarn => Tone == "warn";

    public bool IsBad => Tone == "bad";
}

/// <summary>One marking's holding: the class, its count, and a bar scaled to the
/// largest.</summary>
public sealed record Segment(SecurityClass Class, string Label, int Count, double Width);

/// <summary>One of the most-borrowed titles.</summary>
public sealed record PopularBook(int Rank, string Title, string Author, int Loans)
{
    public string LoansText => Loans == 1 ? "1 loan" : $"{Loans:N0} loans";

    public bool HasAuthor => Author.Length > 0;
}

/// <summary>One line of the overdue list.</summary>
public record OverdueRow(string Member, string Book, string Accession, DateOnly Due, int Days)
{
    public string DueText => Due.ToString("dd MMM yyyy");

    public string DaysText => Days == 1 ? "1 day" : $"{Days} days";
}
