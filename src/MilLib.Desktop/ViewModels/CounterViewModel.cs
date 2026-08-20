using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MilLib.Core.Data;
using MilLib.Desktop.Services;

namespace MilLib.Desktop.ViewModels;

/// <summary>What the counter is in the middle of.</summary>
public enum Stage
{
    /// <summary>Waiting for a scan. Where the screen spends most of its life.</summary>
    Ready,

    /// <summary>A book came back; its condition is being confirmed.</summary>
    Returning,

    /// <summary>An issue needs a decision — a block, an override, or custody details.</summary>
    Issuing,

    /// <summary>What was typed matches several people.</summary>
    Choosing,
}

/// <summary>
/// The counter, as one screen with one box.
///
/// Everything is scanned into the same place — a member's pass, a book's
/// barcode, an accession number, part of a name — and what happens next is
/// decided from what the thing turned out to be and what state it is in. A book
/// that is out comes back; a book that is in goes out. The operator is never
/// asked to say which of those they meant, because at a counter with somebody
/// waiting they already know and the computer can work it out.
///
/// A book scanned before the member is held for a minute, so the natural
/// book-first order works: scan the book, scan the pass, done.
/// </summary>
public partial class CounterViewModel : ViewModelBase
{
    /// <summary>How long a book scanned before a member waits for the pass.</summary>
    private static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(60);

    [ObservableProperty] private Stage _stage = Stage.Ready;
    [ObservableProperty] private string _scanned = "";
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private string _said = "";
    [ObservableProperty] private bool _saidIsGood = true;

    // ------------------------------------------------------- who is at the desk
    [ObservableProperty] private string _memberName = "";
    [ObservableProperty] private string _memberNumber = "";
    [ObservableProperty] private string _memberCategory = "";
    [ObservableProperty] private string _memberStanding = "";
    [ObservableProperty] private bool _hasMember;

    /// <summary>
    /// What this member is actually cleared for — their own clearance capped at
    /// their category's ceiling, never the personal one on its own.
    ///
    /// On the screen because clearance is the one rule on this counter that no
    /// authority in the building can wave through, and the moment it matters is
    /// the moment somebody is holding out a classified book. Reading it off the
    /// panel beats discovering it from a refusal.
    /// </summary>
    [ObservableProperty] private SecurityClass _memberCleared = SecurityClass.UNCLASSIFIED;

    /// <summary>How many of their permitted loans are already out — "3 of 4 out".</summary>
    [ObservableProperty] private string _memberHolding = "";
    [ObservableProperty] private bool _memberAtLimit;

    [ObservableProperty] private string _memberOwes = "";
    [ObservableProperty] private bool _memberOwesAnything;

    // ------------------------------------------------------------- the return
    [ObservableProperty] private string _returningBook = "";
    [ObservableProperty] private string _returningAccession = "";
    [ObservableProperty] private string _returningFrom = "";
    [ObservableProperty] private string _returningDue = "";
    [ObservableProperty] private string _returningLate = "";
    [ObservableProperty] private bool _returningIsLate;
    [ObservableProperty] private CopyCondition _returnCondition = CopyCondition.GOOD;
    [ObservableProperty] private string _returnRemarks = "";

    /// <summary>The condition it went out in, said plainly beside the box.</summary>
    [ObservableProperty] private string _returningWentOut = "";

    /// <summary>
    /// Whether the condition now chosen is worse than the one it left in.
    ///
    /// Said before the button is pressed rather than after. The flag itself is
    /// raised by the core either way, but a warning that arrives once the loan
    /// is closed tells somebody about a decision they can no longer take back.
    /// </summary>
    [ObservableProperty] private bool _willBeFlagged;

    // -------------------------------------------------------------- the issue
    [ObservableProperty] private string _issuingBook = "";
    [ObservableProperty] private string _issuingAccession = "";
    [ObservableProperty] private string _issuingClassification = "";
    [ObservableProperty] private bool _issuingIsClassified;

    /// <summary>
    /// A unit's own publication, which is the only kind that goes out to a
    /// sub-unit rather than to a person. The box is hidden for everything else:
    /// a field that is blank on ninety-nine issues in a hundred is a field the
    /// eye stops reading, including on the hundredth.
    /// </summary>
    [ObservableProperty] private bool _issuingIsUnitPublication;
    [ObservableProperty] private string _overrideReason = "";
    [ObservableProperty] private string _custodyWitness = "";
    [ObservableProperty] private string _custodySignature = "";
    [ObservableProperty] private string _issuedToSubunit = "";
    [ObservableProperty] private bool _blocked;
    [ObservableProperty] private bool _needsOverride;
    [ObservableProperty] private bool _mayOverride;
    [ObservableProperty] private string _issueProblem = "";

    // ------------------------------------------------------------- the pending
    [ObservableProperty] private string _waitingBook = "";
    [ObservableProperty] private bool _bookIsWaiting;

    private ScannedMember? _member;
    private ScannedCopy? _returning;
    private ScannedCopy? _issuing;
    private ScannedCopy? _pending;
    private DateTime _pendingAt;

    public CounterViewModel()
    {
        Say("Scan a member's pass or a book.", true);
    }

    public ObservableCollection<string> Stops { get; } = [];

    public ObservableCollection<LoanRow> OnLoan { get; } = [];

    public ObservableCollection<MemberMatch> Matches { get; } = [];

    public CopyCondition[] Conditions { get; } = Enum.GetValues<CopyCondition>();

    public bool IsReady => Stage == Stage.Ready;

    public bool IsReturning => Stage == Stage.Returning;

    public bool IsIssuing => Stage == Stage.Issuing;

    public bool IsChoosing => Stage == Stage.Choosing;

    public bool HasStops => Stops.Count > 0;

    public bool HasIssueProblem => IssueProblem.Length > 0;

    /// <summary>
    /// Whether the issue can go ahead at all from here — used for the one button
    /// somebody is about to press, so a blocked issue offers nothing to press.
    /// </summary>
    public bool MayProceed => !Blocked && (!NeedsOverride || MayOverride);

    public bool NothingOnLoan => HasMember && OnLoan.Count == 0;

    partial void OnStageChanged(Stage value)
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsReturning));
        OnPropertyChanged(nameof(IsIssuing));
        OnPropertyChanged(nameof(IsChoosing));
    }

    partial void OnIssueProblemChanged(string value) => OnPropertyChanged(nameof(HasIssueProblem));

    partial void OnBlockedChanged(bool value) => OnPropertyChanged(nameof(MayProceed));

    partial void OnNeedsOverrideChanged(bool value) => OnPropertyChanged(nameof(MayProceed));

    partial void OnMayOverrideChanged(bool value) => OnPropertyChanged(nameof(MayProceed));

    partial void OnReturnConditionChanged(CopyCondition value) =>
        WillBeFlagged = _returning?.OnLoan is { } loan && value.IsWorseThan(loan.IssueCondition);

    // ============================================================== the scan ==

    [RelayCommand]
    private async Task ScanAsync()
    {
        var value = Scanned.Trim();

        Scanned = "";

        if (value.Length == 0 || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var desk = new Desk(db, Session.Preferences);

            switch (await desk.ResolveAsync(value))
            {
                case Scan.Book book:
                    await OnBookAsync(db, desk, book.Copy);
                    break;

                case Scan.Person person:
                    await OnMemberAsync(db, desk, person.Member);
                    break;

                case Scan.Several several:
                    ShowMatches(several.Matches);
                    break;

                default:
                    Clear();
                    Say($"Not recognised — “{value}”. Scan a member's pass or a book barcode.", false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Faults.Record("a counter scan", ex);

            Say(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// A book. What happens to it is decided by whether somebody has it: out
    /// means it is coming back, in means it is going out.
    /// </summary>
    private async Task OnBookAsync(MilLibDbContext db, Desk desk, ScannedCopy book)
    {
        if (book.IsOut)
        {
            StartReturn(book);
            return;
        }

        if (_member is null)
        {
            // Held, so the operator can scan the pass next and have it go
            // through on its own.
            _pending = book;
            _pendingAt = DateTime.Now;

            WaitingBook = $"{Accession(book)} — {book.Title.Name}";
            BookIsWaiting = true;

            Stage = Stage.Ready;

            Say("Now scan the member's pass.", true);
            return;
        }

        await StartIssueAsync(db, desk, book);
    }

    /// <summary>
    /// A member. Sets who is at the desk — and finishes any book that was
    /// scanned first and is still waiting.
    /// </summary>
    private async Task OnMemberAsync(MilLibDbContext db, Desk desk, ScannedMember who)
    {
        await SetMemberAsync(desk, who);

        if (_pending is not null && DateTime.Now - _pendingAt <= PendingFor)
        {
            var waiting = _pending;

            ForgetPending();

            // Re-read it: a minute is long enough for somebody at another desk
            // to have issued the same copy.
            var again = await desk.FindCopyAsync(waiting.Copy.Barcode);

            if (again is not null && !again.IsOut)
            {
                await StartIssueAsync(db, desk, again);
                return;
            }

            Say($"{who.Member.Display} — but that book has gone out in the meantime.", false);
            return;
        }

        ForgetPending();

        Stage = Stage.Ready;

        Say($"{who.Member.Display}. Scan a book.", true);
    }

    // ============================================================== the issue ==

    private async Task StartIssueAsync(MilLibDbContext db, Desk desk, ScannedCopy book)
    {
        _issuing = book;

        IssuingBook = book.Title.Name;
        IssuingAccession = Accession(book);
        IssuingIsClassified = book.Title.SecurityClass.IsClassified();
        IssuingClassification = Words.Of(book.Title.SecurityClass);
        IssuingIsUnitPublication = book.Title.IsUnitPublication;

        OverrideReason = "";
        CustodyWitness = "";
        CustodySignature = "";
        IssuedToSubunit = "";
        IssueProblem = "";

        var evaluation = await new IssuePolicy(db)
            .EvaluateAsync(_member!.Member, _member.Category, book.Copy, book.Title);

        Blocked = evaluation.Blocked;
        NeedsOverride = evaluation.NeedsOverride;
        MayOverride = Session.Can(Ability.CirculationOverride);

        Stops.Clear();

        foreach (var violation in evaluation.Violations)
        {
            Stops.Add(violation.Message);
        }

        OnPropertyChanged(nameof(HasStops));

        // Nothing in the way and nothing to record — the commonest case by a
        // long way, and it should cost one scan and no clicks.
        if (evaluation.Clear && !IssuingIsClassified)
        {
            await CommitIssueAsync(evaluation);
            return;
        }

        Stage = Stage.Issuing;

        Say(Blocked
            ? "This cannot be issued."
            : IssuingIsClassified
                ? "Classified — a witness and a signature are required."
                : MayOverride
                    ? "This needs your authority to go ahead."
                    : "This needs a supervisor.", !Blocked);
    }

    [RelayCommand]
    private async Task ConfirmIssueAsync()
    {
        if (_issuing is null || _member is null || Busy || !MayProceed)
        {
            return;
        }

        if (NeedsOverride && MayOverride && OverrideReason.Trim().Length == 0)
        {
            IssueProblem = "A reason is required to override.";
            return;
        }

        if (IssuingIsClassified
            && (CustodyWitness.Trim().Length == 0 || CustodySignature.Trim().Length == 0))
        {
            IssueProblem = "A witness and a signature are required for classified material.";
            return;
        }

        IssueProblem = "";
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var evaluation = await new IssuePolicy(db)
                .EvaluateAsync(_member.Member, _member.Category, _issuing.Copy, _issuing.Title);

            // Checked again at the moment of commit, not only when the panel was
            // drawn. Between the two, somebody at another desk may have issued
            // this very copy.
            if (evaluation.Blocked)
            {
                Blocked = true;

                Stops.Clear();

                foreach (var violation in evaluation.Violations)
                {
                    Stops.Add(violation.Message);
                }

                OnPropertyChanged(nameof(HasStops));

                Say("That has changed — this cannot be issued now.", false);
                return;
            }

            await CommitIssueAsync(evaluation, db);
        }
        catch (Exception ex)
        {
            Faults.Record("issuing a book", ex);

            IssueProblem = Faults.Explain(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task CommitIssueAsync(IssueEvaluation evaluation, MilLibDbContext? open = null)
    {
        var db = open ?? Workspace.Open();

        try
        {
            var counter = new Counter(db, Session.Preferences);

            var loan = await counter.IssueAsync(
                _member!.Member, _member.Category, _issuing!.Copy, _issuing.Title,
                Session.User!.UserId,
                new IssueTerms(
                    OverrideReason: NeedsOverride ? OverrideReason.Trim() : null,
                    OverrideRules: [.. evaluation.Overridable.Select(v => v.Code)],
                    CustodyWitness: CustodyWitness,
                    CustodySignature: CustodySignature,
                    IssuedToSubunit: IssuedToSubunit));

            Say($"Issued {Accession(_issuing)} — {_issuing.Title.Name}. "
                + $"Due {loan.DueOn:dd MMM yyyy}.", true);

            _issuing = null;

            Stage = Stage.Ready;

            await RefreshMemberAsync(db);
        }
        finally
        {
            if (open is null)
            {
                await db.DisposeAsync();
            }
        }
    }

    // ============================================================= the return ==

    private void StartReturn(ScannedCopy book)
    {
        _returning = book;

        ReturningBook = book.Title.Name;
        ReturningAccession = Accession(book);
        ReturningFrom = book.HeldBy?.Display ?? "someone";
        ReturningDue = book.OnLoan!.DueOn.ToString("dd MMM yyyy");

        var late = DateOnly.FromDateTime(DateTime.Today).DayNumber - book.OnLoan.DueOn.DayNumber;

        ReturningIsLate = late > 0;
        ReturningLate = late switch
        {
            <= 0 => "",
            1 => "1 day late",
            _ => $"{late} days late",
        };

        // The condition it went out in, offered back. Most books come back the
        // way they went; making that the default means the common case is one
        // key, and a changed condition is a deliberate act.
        ReturnCondition = book.OnLoan.IssueCondition;
        ReturnRemarks = "";
        ReturningWentOut = $"It went out {Words.Of(book.OnLoan.IssueCondition).ToLowerInvariant()}";
        WillBeFlagged = false;

        Stage = Stage.Returning;

        Say($"Coming back from {ReturningFrom}. Confirm the condition.", true);
    }

    [RelayCommand]
    private async Task ConfirmReturnAsync()
    {
        if (_returning is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var desk = new Desk(db, Session.Preferences);

            var category = (await desk.FindMemberAsync(_returning.OnLoan!.MemberId))?.Category
                ?? new MemberCategory();

            var counter = new Counter(db, Session.Preferences);

            var outcome = await counter.ReturnAsync(
                _returning.OnLoan, _returning.Copy, _returning.Title, category,
                ReturnCondition, ReturnRemarks, Session.User!.UserId);

            var said = $"Returned {Accession(_returning)} — {_returning.Title.Name}.";

            // The three things worth saying after a return, in the order they
            // matter: the book is damaged, money is owed, somebody is waiting.
            if (outcome.Damaged)
            {
                var steps = outcome.Steps == 1 ? "1 condition step" : $"{outcome.Steps} condition steps";

                Say($"{said} FLAGGED: came back worse than it went out (down {steps}).", false);
            }
            else if (outcome.Fine is not null)
            {
                Say($"{said} Overdue — {Session.Preferences.Money(outcome.Fine.Amount)} recorded "
                    + $"against {ReturningFrom}.", false);
            }
            else if (outcome.HeldFor is not null)
            {
                Say($"{said} Put it aside — somebody is waiting for this title.", true);
            }
            else
            {
                Say(said, true);
            }

            _returning = null;

            Stage = Stage.Ready;

            await RefreshMemberAsync(db);
        }
        catch (Exception ex)
        {
            Faults.Record("returning a book", ex);

            Say(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;
        }
    }

    // ============================================================== the member ==

    [RelayCommand]
    private async Task ChooseAsync(MemberMatch match)
    {
        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var desk = new Desk(db, Session.Preferences);

            var who = await desk.FindMemberAsync(match.MemberId);

            if (who is not null)
            {
                await SetMemberAsync(desk, who);

                Stage = Stage.Ready;

                Say($"{who.Member.Display}. Scan a book.", true);
            }
        }
        catch (Exception ex)
        {
            Faults.Record("choosing a member", ex);

            Say(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task RenewAsync(LoanRow row)
    {
        if (_member is null || Busy)
        {
            return;
        }

        Busy = true;

        try
        {
            await using var db = Workspace.Open();

            var loan = await new Desk(db, Session.Preferences).FindCopyAsync(row.Barcode);

            if (loan?.OnLoan is null)
            {
                Say("That loan is no longer open.", false);
                return;
            }

            var counter = new Counter(db, Session.Preferences);

            var why = await counter.WhyNotRenewableAsync(loan.OnLoan, _member.Category, loan.Copy.TitleId);

            if (why is not null)
            {
                Say(why, false);
                return;
            }

            var renewal = await counter.RenewAsync(loan.OnLoan, _member.Category, Session.User!.UserId);

            Say($"Renewed {row.Accession}. Now due {renewal.NewDueOn:dd MMM yyyy}.", true);

            await RefreshMemberAsync(db);
        }
        catch (Exception ex)
        {
            Faults.Record("renewing a loan", ex);

            Say(Faults.Explain(ex), false);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Done with this member — the next person steps up to a clean desk.</summary>
    [RelayCommand]
    private void Finish()
    {
        _member = null;
        _issuing = null;
        _returning = null;

        ForgetPending();

        HasMember = false;
        MemberName = "";
        MemberNumber = "";
        MemberCategory = "";
        MemberStanding = "";
        MemberHolding = "";
        MemberAtLimit = false;
        MemberOwes = "";
        MemberOwesAnything = false;
        MemberCleared = SecurityClass.UNCLASSIFIED;

        OnLoan.Clear();
        Matches.Clear();
        Stops.Clear();

        OnPropertyChanged(nameof(HasStops));
        OnPropertyChanged(nameof(NothingOnLoan));

        Stage = Stage.Ready;

        Say("Scan a member's pass or a book.", true);
    }

    /// <summary>Backing out of a panel without doing anything.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _issuing = null;
        _returning = null;

        Stage = Stage.Ready;

        Say(HasMember ? "Cancelled. Scan a book." : "Cancelled. Scan a member's pass or a book.", true);
    }

    private async Task SetMemberAsync(Desk desk, ScannedMember who)
    {
        _member = who;

        HasMember = true;
        MemberName = who.Member.Display;
        MemberNumber = who.Member.MembershipNo;
        // The ceiling on how many books goes with the running count rather than
        // here, so the two numbers that have to be read together are read
        // together — "3 of 4 out" rather than a limit in one line and a tally
        // somewhere else on the panel.
        MemberCategory = $"{who.Category.Name} — {who.Category.LoanDays} days out"
            + (who.Category.MaxRenewals > 0
                ? $", {who.Category.MaxRenewals} renewal{(who.Category.MaxRenewals == 1 ? "" : "s")}"
                : ", no renewals");

        MemberCleared = who.Member.EffectiveClearance(who.Category);

        MemberStanding = who.Member.Status == MemberStatus.ACTIVE
            ? ""
            : Words.Of(who.Member.Status);

        Matches.Clear();

        await LoadLoansAsync(desk);
    }

    private async Task RefreshMemberAsync(MilLibDbContext db)
    {
        if (_member is null)
        {
            return;
        }

        await LoadLoansAsync(new Desk(db, Session.Preferences));
    }

    private async Task LoadLoansAsync(Desk desk)
    {
        OnLoan.Clear();

        var today = DateOnly.FromDateTime(DateTime.Today);

        foreach (var (loan, copy, title) in await desk.OpenLoansAsync(_member!.Member.MemberId))
        {
            OnLoan.Add(new LoanRow(
                loan.LoanId,
                copy.Barcode,
                Session.Preferences.Accession(copy.AccessionNo),
                title.Name,
                loan.DueOn,
                today.DayNumber - loan.DueOn.DayNumber,
                loan.RenewalCount,
                _member.Category.MaxRenewals));
        }

        var permitted = _member.Category.MaxBooks;

        MemberHolding = $"{OnLoan.Count} of {permitted} out";
        MemberAtLimit = OnLoan.Count >= permitted;

        var owed = await desk.OwedAsync(_member.Member.MemberId);

        MemberOwesAnything = owed > 0m;
        MemberOwes = owed > 0m ? $"{Session.Preferences.Money(owed)} owed" : "";

        OnPropertyChanged(nameof(NothingOnLoan));
    }

    private void ShowMatches(IReadOnlyList<Member> matches)
    {
        Matches.Clear();

        foreach (var member in matches)
        {
            Matches.Add(new MemberMatch(member.MemberId, member.Display, member.MembershipNo,
                member.UnitCoy ?? ""));
        }

        Stage = Stage.Choosing;

        Say($"{matches.Count} people match that. Which one?", true);
    }

    private void ForgetPending()
    {
        _pending = null;
        BookIsWaiting = false;
        WaitingBook = "";
    }

    private void Clear() => ForgetPending();

    private void Say(string what, bool good)
    {
        Said = what;
        SaidIsGood = good;
    }

    private static string Accession(ScannedCopy book) =>
        Session.Preferences.Accession(book.Copy.AccessionNo);
}

/// <summary>One book this member is holding.</summary>
public record LoanRow(
    long LoanId, string Barcode, string Accession, string Title,
    DateOnly Due, int Late, int Renewals, int MaxRenewals)
{
    public string DueText => Due.ToString("dd MMM yyyy");

    public bool IsLate => Late > 0;

    public string LateText => Late switch
    {
        <= 0 => $"due {DueText}",
        1 => "1 day late",
        _ => $"{Late} days late",
    };

    public string RenewalsText => MaxRenewals == 0
        ? "no renewals allowed"
        : $"renewed {Renewals} of {MaxRenewals}";

    public bool MayRenew => Renewals < MaxRenewals;
}

/// <summary>One of several people who match what was typed.</summary>
public record MemberMatch(long MemberId, string Name, string Number, string Unit);
