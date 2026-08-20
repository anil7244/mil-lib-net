using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using MilLib.Core.Data;
using MilLib.Core.Documents;
using MilLib.Desktop;
using MilLib.Desktop.Services;
using MilLib.Desktop.ViewModels;
using MilLib.Desktop.Views;
using QuestPDF.Fluent;

// A picture of a screen, taken without a person and without a login.
//
// Two kinds of screen are shot here. Some — the counter — are decided entirely
// by data the view-model is handed directly, so they need no database. Others —
// the catalogue — read the real library as they load, so this points the
// application at the real file, signs a session in the way the front door does,
// and lets the screen fill itself. Either way there is no window to click and
// no pass to scan; the machine that runs the tests has neither.
//
//   D:\dotnet10\dotnet.exe run --project tools\MilLib.ViewShot -- <out-dir>

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : AppContext.BaseDirectory;

        Directory.CreateDirectory(outDir);

        // The same licence the application declares at startup — needed before
        // QuestPDF will render the pass to an image.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        // The screens that read the catalogue need it beside them, and a session
        // signed in — the same two things the real front door arranges before
        // any of these screens is ever shown.
        var live = PointAtTheRealLibrary();

        Shoot(Path.Combine(outDir, "counter-return.png"), Returning());
        Shoot(Path.Combine(outDir, "counter-issue.png"), IssuingUnitPublication());

        if (live)
        {
            var books = new BooksViewModel();

            SettleWhile(() => books.Busy);

            Shoot(Path.Combine(outDir, "books-list.png"), new BooksView { DataContext = books });

            // The imported library holds nothing classified and no unit
            // publications, so the marks that exist for them cannot be shown
            // from it. Two made-up rows at the top of the real list prove the
            // marks draw — the rest of the list is the actual catalogue.
            books.Shown.Insert(0, new BookRow(
                -1, "Standing Orders — 15 Mountain Brigade", "2026 revision",
                "Brigade Headquarters", "Brigade Press", "English", "U-001",
                "JAKLI/2210", "Booklet", 2026, true,
                SecurityClass.RESTRICTED, 3, 2));

            books.Shown.Insert(1, new BookRow(
                -2, "Order of Battle — Northern Sector", "",
                "Directorate of Military Intelligence", "", "English", "355.4",
                "JAKLI/2211", "Map", 2025, false,
                SecurityClass.SECRET, 1, 0));

            Shoot(Path.Combine(outDir, "books-list-marked.png"), new BooksView { DataContext = books });

            var titleId = InterestingTitle();

            if (titleId is { } id)
            {
                var window = new BookWindow(id);

                SettleWhile(() => ((BookViewModel)window.DataContext!).Busy);

                ShootWindow(Path.Combine(outDir, "book-show.png"), window);
            }

            // Subjects: the imported library has none, so a small tree is put
            // on the throwaway copy to show the screen doing its job.
            SeedSubjects();

            var subjects = new SubjectsViewModel();

            SettleWhile(() => subjects.Busy);

            subjects.Selected = subjects.Headings.FirstOrDefault(h => h.Depth == 0);

            SettleWhile(() => !subjects.Editing);

            Shoot(Path.Combine(outDir, "subjects.png"), new SubjectsView { DataContext = subjects });

            // Stock check: none in the imported library, so one is started on the
            // throwaway copy and a handful of shelves scanned, so the scan panel
            // and its running counts can be shown.
            SeedStockCheck();

            var stock = new StockViewModel();

            SettleWhile(() => stock.Busy);

            stock.Selected = stock.Checks.FirstOrDefault();

            SettleWhile(() => !stock.IsOpen);

            Shoot(Path.Combine(outDir, "stock.png"), new StockView { DataContext = stock });

            var labels = new LabelsViewModel();

            SettleWhile(() => labels.Busy);

            foreach (var r in labels.Rows.Take(6))
            {
                r.IsChosen = true;
            }
            labels.Changed();

            Shoot(Path.Combine(outDir, "labels.png"), new LabelsView { DataContext = labels });

            // An actual sheet of pocket labels, both codes, as it prints.
            var sheet = ShootLabels();
            if (sheet is not null)
            {
                File.WriteAllBytes(Path.Combine(outDir, "labels-sheet.png"), sheet);
            }

            var reg = new RegisterViewModel();

            SettleWhile(() => reg.Busy);

            Shoot(Path.Combine(outDir, "register.png"), new RegisterView { DataContext = reg });

            // The printed ledger, exactly as it prints, so the full statutory
            // columns can be checked.
            var regDoc = ShootRegister();
            if (regDoc is not null)
            {
                File.WriteAllBytes(Path.Combine(outDir, "register-print.png"), regDoc);
            }

            var dash = new DashboardViewModel();

            SettleWhile(() => dash.Busy);

            Shoot(Path.Combine(outDir, "dashboard.png"), new DashboardView { DataContext = dash });

            var reports = new ReportsViewModel();

            SettleWhile(() => reports.Busy);

            // Holdings always has rows, so the shot shows a filled table rather
            // than the "nothing overdue" the imported data happens to give.
            reports.Chosen = reports.Choices.FirstOrDefault(c => c.Kind == ReportKind.Holdings)
                ?? reports.Chosen;

            SettleWhile(() => reports.Busy || !reports.HasReport);

            Shoot(Path.Combine(outDir, "reports.png"), new ReportsView { DataContext = reports });

            var members = new MembersViewModel();

            SettleWhile(() => members.Busy);

            // Pick somebody so the detail panel — the half this pass changed —
            // is the half that shows. The screen loads the person on its own
            // once the row is set.
            members.Selected = members.Shown.FirstOrDefault();

            SettleWhile(() => members.Who.Length == 0);

            Shoot(Path.Combine(outDir, "members.png"), new MembersView { DataContext = members });

            // Fines: the imported library has none pending, so a couple are put
            // on the throwaway copy to show the row — an overdue charge with its
            // span, and a flat damage charge.
            SeedFines();

            var fines = new FinesViewModel();

            SettleWhile(() => fines.Busy);

            Shoot(Path.Combine(outDir, "fines.png"), new FinesView { DataContext = fines });

            // Reservations: none in the imported library, so a ready hold and a
            // couple of waiting ones are put on the throwaway copy to show both
            // lists working.
            SeedHolds();

            var holds = new ReservationsViewModel();

            SettleWhile(() => holds.Busy);

            Shoot(Path.Combine(outDir, "reservations.png"), new ReservationsView { DataContext = holds });

            // The pass, drawn exactly as the on-screen preview draws it — the
            // print document rendered to an image. Uses the real member with a
            // photograph so the face, the QR and the crest all show.
            var passes = ShootPass();

            if (passes is not null)
            {
                File.WriteAllBytes(Path.Combine(outDir, "pass-card.png"), passes);
            }

            // ---- the administration group -----------------------------------

            var rules = new LendingRulesViewModel();

            SettleWhile(() => rules.Busy);

            rules.Selected = rules.Rules.FirstOrDefault();

            Shoot(Path.Combine(outDir, "admin-lending-rules.png"),
                new LendingRulesView { DataContext = rules });

            var staff = new StaffViewModel();

            SettleWhile(() => staff.Busy);

            staff.Selected = staff.People.FirstOrDefault();

            Shoot(Path.Combine(outDir, "admin-staff.png"),
                new StaffView { DataContext = staff });

            var activity = new ActivityViewModel();

            SettleWhile(() => activity.Busy);

            Shoot(Path.Combine(outDir, "admin-activity.png"),
                new ActivityView { DataContext = activity });
        }

        Console.WriteLine($"Wrote screenshots to {outDir}"
            + (live ? "" : " (catalogue skipped — no database found beside the tool)"));

        return 0;
    }

    // ---------------------------------------------------------- the real file --

    /// <summary>
    /// Put the real library where this tool will look, and sign in. Returns
    /// false when there is no database to find, so the catalogue shots are
    /// simply skipped rather than drawn empty.
    /// </summary>
    private static bool PointAtTheRealLibrary()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "data", "database.sqlite");

        if (!File.Exists(here))
        {
            string[] known =
            [
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "data", "database.sqlite")),
                @"D:\mil-lib-net\app\data\database.sqlite",
            ];

            var source = known.FirstOrDefault(File.Exists);

            if (source is null)
            {
                return false;
            }

            var folder = Path.GetDirectoryName(source)!;
            var target = Path.GetDirectoryName(here)!;

            Directory.CreateDirectory(target);

            // The database, and the pictures beside it — a crest and any covers —
            // so the catalogue shows what the unit actually sees.
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }

        // The photos and covers live in the web application's storage tree, not
        // beside the database — the conversion to a single data folder did not
        // bring them across. For a truthful shot they are pulled in here so the
        // resolver finds them; the running application would need the same files
        // in its data folder to show a face or a cover.
        var pictures = @"D:\XAMPP\htdocs\mil-lib-sqlite\public\storage";

        if (Directory.Exists(pictures))
        {
            var dataDir = Path.GetDirectoryName(here)!;

            foreach (var sub in new[] { "member-photos", "covers" })
            {
                var from = Path.Combine(pictures, sub);

                if (!Directory.Exists(from))
                {
                    continue;
                }

                var to = Path.Combine(dataDir, sub);

                Directory.CreateDirectory(to);

                foreach (var file in Directory.EnumerateFiles(from))
                {
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
                }
            }
        }

        Workspace.Forget();

        using var db = Workspace.Open();

        var user = db.Users.OrderBy(u => u.UserId).First();

        Session.Begin(user, Preferences.ReadAsync(db).GetAwaiter().GetResult());

        Theming.Apply(Session.Preferences);

        return true;
    }

    /// <summary>
    /// A title worth opening: a unit publication for its amendment line, else a
    /// classified one for its marking, else simply one that has copies.
    /// </summary>
    private static long? InterestingTitle()
    {
        using var db = Workspace.Open();

        var unit = db.Titles
            .Where(t => t.IsUnitPublication && t.Copies.Any())
            .Select(t => (long?)t.TitleId)
            .FirstOrDefault();

        var classified = db.Titles
            .Where(t => t.SecurityClass != SecurityClass.UNCLASSIFIED && t.Copies.Any())
            .Select(t => (long?)t.TitleId)
            .FirstOrDefault();

        var any = db.Titles
            .Where(t => t.Copies.Any())
            .OrderBy(t => t.Name)
            .Select(t => (long?)t.TitleId)
            .FirstOrDefault();

        return unit ?? classified ?? any;
    }

    /// <summary>A small subject tree with a few titles filed, for the shot.</summary>
    private static void SeedSubjects()
    {
        using var db = Workspace.Open();

        db.TitleCategories.RemoveRange(db.TitleCategories);
        db.Categories.RemoveRange(db.Categories);
        db.SaveChanges();

        Category Add(string name, long? parent, int order)
        {
            var c = new Category { Name = name, ParentId = parent, SortOrder = order };
            db.Categories.Add(c);
            db.SaveChanges();
            return c;
        }

        var history = Add("Military History", null, 0);
        Add("Regimental Histories", history.CategoryId, 0);
        var campaigns = Add("Campaigns & Operations", history.CategoryId, 1);
        Add("Mountain Warfare", campaigns.CategoryId, 0);
        Add("Leadership & Training", null, 1);
        Add("Reference & General", null, 2);

        // File a handful of real titles under the top heading.
        var titleIds = db.Titles.OrderBy(t => t.TitleId).Select(t => t.TitleId).Take(4).ToList();

        foreach (var id in titleIds)
        {
            db.TitleCategories.Add(new TitleCategory { TitleId = id, CategoryId = history.CategoryId });
        }

        db.SaveChanges();
    }

    /// <summary>An in-progress stock check with a few shelves scanned, for the shot.</summary>
    private static void SeedStockCheck()
    {
        using var db = Workspace.Open();

        db.StockVerificationScans.RemoveRange(db.StockVerificationScans);
        db.StockVerifications.RemoveRange(db.StockVerifications);
        db.SaveChanges();

        var stock = new StockCheck(db);
        var today = DateOnly.FromDateTime(DateTime.Today);

        var check = stock.StartAsync("Stock Verification 2026", Session.User!.UserId, null, today)
            .GetAwaiter().GetResult();

        // Scan the first several copies as if walking the shelf, and one number
        // that is not in the register, so a stranger shows too.
        var barcodes = db.Copies.OrderBy(c => c.CopyId).Select(c => c.Barcode).Take(40).ToList();

        foreach (var barcode in barcodes)
        {
            stock.ScanAsync(check, barcode, Session.User.UserId).GetAwaiter().GetResult();
        }

        stock.ScanAsync(check, "9999-NOT-A-BOOK", Session.User.UserId).GetAwaiter().GetResult();
    }

    /// <summary>A sheet of pocket labels (barcode and QR), as an image.</summary>
    private static byte[]? ShootLabels()
    {
        using var db = Workspace.Open();

        var labelling = new Labelling(db, Session.Preferences);

        var found = labelling.FindAsync("").GetAwaiter().GetResult();

        if (found.Count == 0)
        {
            return null;
        }

        var books = found.Take(8).Select(x => new LabelFor(
            Session.Preferences.Accession(x.Copy.AccessionNo),
            x.Copy.Barcode, x.Title.Name, x.Title.CallNumber ?? "")).ToList();

        return new LabelSheetDocument(Letterheads.Current(), books,
                LabelKind.Pocket, LabelCode.Both,
                labelling.PocketWidthMm, labelling.PocketHeightMm)
            .GenerateImages(new QuestPDF.Infrastructure.ImageGenerationSettings { RasterDpi = 170 })
            .First();
    }

    /// <summary>The first page of the printed accession register, as an image.</summary>
    private static byte[]? ShootRegister()
    {
        using var db = Workspace.Open();

        var reg = new Register(db, Session.Preferences);

        var (first, _) = reg.ExtentAsync().GetAwaiter().GetResult();

        var entries = reg.ReadAsync(first, first + 9).GetAwaiter().GetResult();

        if (entries.Count == 0)
        {
            return null;
        }

        return new AccessionRegisterDocument(Letterheads.Current(), entries,
                "1 to 8", Session.Preferences.CurrencySymbol)
            .GenerateImages(new QuestPDF.Infrastructure.ImageGenerationSettings { RasterDpi = 170 })
            .First();
    }

    /// <summary>A ready hold and two waiting ones, for the Reservations shot.</summary>
    private static void SeedHolds()
    {
        using var db = Workspace.Open();

        var member = db.Members.OrderBy(m => m.MemberId).First();

        db.Reservations.RemoveRange(db.Reservations);
        db.SaveChanges();

        var now = new DateTime(2026, 8, 20, 9, 0, 0);
        var today = DateOnly.FromDateTime(now);

        // A ready hold: a copy set aside for this member, kept a few days.
        var readyCopy = db.Copies.OrderBy(c => c.CopyId).First();

        db.Reservations.Add(new Reservation
        {
            TitleId = readyCopy.TitleId,
            MemberId = member.MemberId,
            ReservedOn = now.AddDays(-2),
            QueuePosition = 1,
            Status = ReservationStatus.READY,
            ReadyOn = now.AddDays(-1),
            ExpiresOn = today.AddDays(2),
            FulfilledCopyId = readyCopy.CopyId,
        });

        // Two waiting on a different, fully-out title.
        var otherTitle = db.Titles.OrderByDescending(t => t.TitleId).First();

        db.Reservations.Add(new Reservation
        {
            TitleId = otherTitle.TitleId, MemberId = member.MemberId,
            ReservedOn = now.AddDays(-1), QueuePosition = 1, Status = ReservationStatus.WAITING,
        });

        db.SaveChanges();
    }

    /// <summary>A pending overdue fine and a damage fine, for the Fines shot.</summary>
    private static void SeedFines()
    {
        using var db = Workspace.Open();

        var member = db.Members.OrderBy(m => m.MemberId).First();

        var loanId = db.Loans
            .Where(l => l.MemberId == member.MemberId)
            .Select(l => (long?)l.LoanId)
            .FirstOrDefault();

        var today = DateOnly.FromDateTime(DateTime.Today);

        db.Fines.RemoveRange(db.Fines.Where(f => f.MemberId == member.MemberId));
        db.SaveChanges();

        db.Fines.Add(new Fine
        {
            MemberId = member.MemberId,
            LoanId = loanId,
            Type = FineType.OVERDUE,
            Amount = 40m,
            DaysOverdue = 10,
            CalculatedOn = today,
            Status = FineStatus.PENDING,
        });

        db.Fines.Add(new Fine
        {
            MemberId = member.MemberId,
            LoanId = loanId,
            Type = FineType.DAMAGE,
            Amount = 250m,
            CalculatedOn = today,
            Status = FineStatus.PENDING,
            Remarks = "Cover detached, pages loose",
        });

        db.SaveChanges();
    }

    /// <summary>The single-card pass image, for the member who has a photo.</summary>
    private static byte[]? ShootPass()
    {
        using var db = Workspace.Open();

        var withPhoto = db.Members
            .Where(m => m.PhotoPath != null && m.PhotoPath != "")
            .Select(m => m.MemberId)
            .FirstOrDefault();

        var id = withPhoto != 0
            ? withPhoto
            : db.Members.Select(m => m.MemberId).FirstOrDefault();

        if (id == 0)
        {
            return null;
        }

        var passes = new Roll(db).PassesForAsync([id]).GetAwaiter().GetResult();

        if (passes.Count == 0)
        {
            return null;
        }

        var member = passes[0] with { PhotoPath = Workspace.CoverPath(passes[0].PhotoPath) };

        return new PassDocument(Letterheads.Current(), [member], singleCard: true)
            .GenerateImages(new QuestPDF.Infrastructure.ImageGenerationSettings { RasterDpi = 300 })
            .First();
    }

    // --------------------------------------------------------------- counter --

    /// <summary>A member at the desk, a classified book coming back damaged.</summary>
    private static CounterViewModel Returning()
    {
        var vm = WithMember();

        vm.ReturningBook = "Field Regulations for Mountain Warfare, Vol II";
        vm.ReturningAccession = "JAKLI/1042";
        vm.ReturningFrom = "MAJ KARTIK";
        vm.ReturningDue = "12 Aug 2026";
        vm.ReturningLate = "8 days late";
        vm.ReturningIsLate = true;
        vm.ReturningWentOut = "It went out good";
        vm.ReturnCondition = CopyCondition.POOR;
        vm.WillBeFlagged = true;
        vm.Stage = Stage.Returning;

        return vm;
    }

    /// <summary>A member at the desk, a unit publication going out.</summary>
    private static CounterViewModel IssuingUnitPublication()
    {
        var vm = WithMember();

        vm.IssuingBook = "Unit Standing Orders 2026";
        vm.IssuingAccession = "JAKLI/2210";
        vm.IssuingIsClassified = false;
        vm.IssuingIsUnitPublication = true;
        vm.Stage = Stage.Issuing;

        return vm;
    }

    private static CounterViewModel WithMember()
    {
        var vm = new CounterViewModel
        {
            HasMember = true,
            MemberName = "MAJ KARTIK SHARMA",
            MemberNumber = "M0001",
            MemberCategory = "Officer — 30 days out, 2 renewals",
            MemberCleared = SecurityClass.RESTRICTED,
            MemberHolding = "3 of 4 out",
            MemberAtLimit = false,
            MemberOwes = "\u20B955.00 owed",
            MemberOwesAnything = true,
        };

        vm.OnLoan.Add(new LoanRow(1, "1001", "JAKLI/1001",
            "Small Arms Handling and Marksmanship",
            new DateOnly(2026, 9, 1), -12, 0, 2));

        vm.OnLoan.Add(new LoanRow(2, "1042", "JAKLI/1042",
            "Field Regulations for Mountain Warfare, Vol II",
            new DateOnly(2026, 8, 12), 8, 1, 2));

        vm.OnLoan.Add(new LoanRow(3, "1200", "JAKLI/1200",
            "Military History of the Northern Sector",
            new DateOnly(2026, 9, 20), -31, 2, 2));

        return vm;
    }

    // ----------------------------------------------------------------- shutter --

    private static void Shoot(string path, object content)
    {
        const int width = 1180;
        const int height = 760;

        var window = new Window
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.Parse("#F4F5F7")),
            Content = content,
            Padding = new Thickness(20),
        };

        Capture(window, path, width, height);
    }

    private static void ShootWindow(string path, Window window)
    {
        const int width = 1180;
        const int height = 840;

        window.Width = width;
        window.Height = height;

        Capture(window, path, width, height);
    }

    private static void Capture(Window window, string path, int width, int height)
    {
        window.Show();

        Dispatcher.UIThread.RunJobs();

        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));

        Dispatcher.UIThread.RunJobs();

        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));

        bitmap.Render(window);
        bitmap.Save(path);

        window.Close();
    }

    /// <summary>
    /// Pump the dispatcher until a screen has finished loading, or a couple of
    /// seconds have passed — long enough for a database read, short enough that
    /// a stuck one does not hang the tool.
    /// </summary>
    private static void SettleWhile(Func<bool> busy)
    {
        for (var i = 0; i < 80 && busy(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
