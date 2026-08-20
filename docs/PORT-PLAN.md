# Military Library Management System — the desktop application

The same library, as a native Windows program: no web server, no browser, no
port, no PHP. One executable and a folder of records beside it.

It is built the way **TCE Licence and Billing Manager** is built, deliberately —
same framework, same shape, same design system — so that a person who has
maintained one can maintain the other, and a unit that has both is not looking
at two different pieces of software.

## What it is made of

| | |
|---|---|
| Framework | .NET 10 + Avalonia 12 (Fluent), MVVM via CommunityToolkit |
| Data | EF Core 9 over SQLite; MySQL/MariaDB as an option on the Database screen |
| Documents | QuestPDF, QRCoder, SkiaSharp |
| Passwords | BCrypt, cost 12 — the same hash the PHP application writes |
| Shipped as | one self-contained single-file `.exe`, no runtime to install |

Two projects:

- **`src/MilLib.Core`** — the records and the rules. Knows nothing about a
  screen. Entities, the DbContext, the ability matrix, sign-in, the settings,
  and (as they land) circulation, accessioning and the printed documents.
- **`src/MilLib.Desktop`** — the windows. Views, view models, the design system
  carried over from TCE, and the services that know about this machine:
  where the data file is, what has gone wrong, and what to back up.

## The build

The system `dotnet` on this machine is 9.0.317 and **cannot build this**. Use
the .NET 10 SDK at `D:\dotnet10`:

```bash
D:\dotnet10\dotnet.exe build D:\mil-lib-net\MilLib.sln
```

To run the framework-dependent build, `DOTNET_ROOT` must point at it too, or
Windows offers to install .NET 10. The shipped build is self-contained and has
no such problem.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.DataProof
```

`MilLib.DataProof` reads every table, every awkward column and every join, and
says so line by line. Run it after any change to the entities or the DbContext:
a wrong mapping does not fail at build time and often does not fail at startup
either — it fails on the one screen nobody tried.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.CounterProof
```

`MilLib.CounterProof` puts the counter loop through issue, renew, return,
damage, overdue fines, clearance, the borrowing ceiling and the scan box, on a
scratch copy of the real library that it deletes afterwards. These are the rules
the library is judged on and they now exist in two applications; run this after
touching anything under `Circulation/`.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.RollProof
```

`MilLib.RollProof` does the same for the roll and the lending rules: the
clearance ceiling, duplicate membership numbers, what blocks a no-dues chit,
what may and may not be deleted.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.AccessionProof
```

`MilLib.AccessionProof` checks the one guarantee the register rests on:
sequential, gap-free, never reused — including with eight callers accessioning
at the same moment on eight connections. It also checks what may be changed
about a copy afterwards, and what may not.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.PrintProof
```

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.ReportProof
```

`MilLib.ReportProof` runs every report twice — once as somebody cleared for
everything, once as somebody cleared for nothing — and compares. No report may
show material above the clearance of the person who asked for it, and the report
that gets that wrong is the one noticed in a board of enquiry rather than in
testing.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.StockProof
```

`MilLib.StockProof` counts a shelf of ten with one book issued mid-count, one at
the binder, one scanned twice and one barcode nobody knows — and checks what
each of those does to the "not found" figure. A book wrongly declared missing
wastes a board's time and somebody's reputation; one wrongly declared present is
worse.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.WithdrawalProof
```

`MilLib.WithdrawalProof` checks what condemnation refuses to do — condemn a book
somebody is holding, reuse a withdrawal number, condemn the same copy twice,
delete anything — and what it does to the borrower when a book really is written
off against them.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.QueueProof
```

`MilLib.QueueProof` puts three people in a queue for one copy and follows it
through: who gets offered it, who may take it off the hold shelf, what happens
when nobody collects. Then the fines: raised, paid against a receipt, waived
with a reason, and settleable only once.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.SystemProof
```

`MilLib.SystemProof` covers the two places a mistake cannot be undone from
inside the application. It tries to move the accession starting number on a
library that already has books on the register, and checks that the refusal
happened before anything at all was written. Then it tries all four lockout
routes in turn — suspend yourself, suspend the last administrator, demote them
instead, change your own role — and confirms every one is refused and that the
lock lifts the moment there is a second administrator. Finally it reads the
activity log back: the filters, the paging, and that an ordinary sign-in is
*not* marked as notable.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.CatalogueProof
```

`MilLib.CatalogueProof` holds down the rule the whole data model rests on: a
title is a description, and cataloguing one creates no copy, spends no
accession number and puts nothing on the register. Then the authority lists —
the same publisher typed in capitals, the same author typed in lower case, the
same person entered twice on one form — and what removal refuses to do once a
copy exists.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.SubjectProof
```

`MilLib.SubjectProof` proves the tree stays a tree. A heading cannot be filed
under itself or under anything already beneath it, and one with books or
headings under it cannot be deleted out from under them. Then it puts a ring
into the table past every guard — which is what an older version or a database
tool could leave behind — and checks that the walk finishes and says how many
headings it could not reach.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.BackupProof
```

`MilLib.BackupProof` is about the one failure that cannot be undone by
retyping. It checks that a copy is a whole working library — settings, roll and
history, and SQLite's own integrity check — that a copy taken **while a write is
still outstanding in the write-ahead log** carries that write, and that putting
one back really replaces what was there and can itself be undone. Then it does a
restore wrongly on purpose, leaving the old log in place, because that is the
failure that leaves no trace: the restore appears to work and changes nothing.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.LicenceProof
```

`MilLib.LicenceProof` starts from the only thing that really matters about
licensing: two programs have to produce the same key. Its expected values were
generated by running the PHP generator itself, not by running this code and
writing down what came out. After that it checks that a key is for one machine
only, that the date on it cannot be edited, and that neither a copied
`license_info` row nor a made-up key typed straight into the table licences
anything.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.KioskProof
```

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net	ools\MilLib.ServerProof
```

`MilLib.ServerProof` is about the three places a unit's records can live: a
file beside the application, a MariaDB everyone shares, or a PostgreSQL their IT
cell insists on. It needs no server running, because what it checks is what goes
wrong silently — a default port nobody typed, a connection string missing the
one setting that stops every due date sliding by the machine's offset, a table
name that is fine on MySQL and unfindable on PostgreSQL because it carries a
capital. None of those show up until a unit has already moved.

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net	ools\MilLib.BrandProof
```

`MilLib.BrandProof` guards the thing that makes this sellable to more than one
unit. The name, crest and colours are not preferences to a buyer — they are what
their headed paper looks like — so the one thing that must never happen is a
save that quietly replaces them with somebody else's. It nearly did: a colour
the code could not read was written down as the house red rather than refused,
so a half-typed hex code, or a form saved before it had finished loading,
rebranded the unit silently and left an audit entry saying it was deliberate.

Building the copy that goes to a unit:

```bash
powershell -File D:\mil-lib-net\tools\publish.ps1 -Fresh
```

```bash
D:\dotnet10\dotnet.exe run --project D:\mil-lib-net\tools\MilLib.ZebraProof
```

`MilLib.ZebraProof` settles everything about the thermal labels that can be
settled without the printer: that every dimension comes from the stock size and
the printer's resolution rather than from dot counts compiled in, that the same
label at 300 dpi is the same *physical* size as at 203, that nothing is placed
outside the label, and that a book title carrying a ZPL control character
cannot turn the rest of it into instructions.

`MilLib.KioskProof` is mostly about what the reading-room terminal must **not**
do, because it is the only screen in the application a stranger can reach: show
anything classified to somebody who has not scanned a pass, show one member
anything about another, or let somebody hold a book they are not cleared to know
exists. It also checks the ordinary thing — that "is it in?" is answered in
words rather than in two numbers to work out.

`MilLib.PrintProof` renders the documents for real against the real library and
**leaves a picture of the first page** in `%TEMP%\Library Documents`. Look at
it. The faults a printed document actually has — a column overflowing, a
heading colliding with the crest, a table running off the paper — are invisible
to every check that only counts rows.

## The records

`app/data/database.sqlite` holds the real library: **1,397 titles, 1,555 copies**
(unit prefix JAKLI), the member categories, the settings and the audit log,
carried over from the MySQL database the web application uses.

The schema is not written by this application and it owns no migrations. It is
built by running the PHP application's own migrations against the file, so the
two cannot drift:

```bash
cd D:\xampp\htdocs\mil-lib
DB_CONNECTION=sqlite DB_DATABASE=D:\mil-lib-net\app\data\database.sqlite D:\php84\php.exe artisan migrate --force
D:\php84\php.exe D:\mil-lib-net\tools\carry-over-from-mysql.php
```

The carry-over replaces; it does not merge. Two libraries that have both been
worked in have two different ideas of which book is out, and there is no
sensible way to reconcile that automatically. **Work in one at a time.**

## Two rules that are expensive to undo

Both are inherited from the specification and both are easy to get wrong here:

- **No `quantity` on a title.** A title is the work; a copy is the object on the
  shelf, with its own accession number and barcode. Circulation, stock audit and
  loss all hang off individual copies.
- **A feature flag hides a screen, never a table.** Every install carries the
  whole schema whichever flags are set. Turning one on reveals a screen; it
  never requires a migration.

## Where the work stands

### Done

- [x] Solution, both projects, the design system and the icon set
- [x] All 24 entities mapped onto the Laravel schema, with the enum, date and
      money conversions SQLite needs
- [x] The ability matrix, copied from the PHP application and proved against it
- [x] Settings, branding and feature flags read from the database
- [x] Sign-in against the same accounts and the same bcrypt hashes
- [x] The carry-over tool, and `MilLib.DataProof` to check its work
- [x] The shell: crest, ability- and feature-driven menu, sign-out, data file
- [x] **Home** — the six figures and the overdue list
- [x] **Books in Library** — the whole catalogue, searchable
- [x] The circulation rules — the issue policy, the fine calculation, the holds
      queue, the two ladders (clearance and condition) — copied from the PHP and
      proved against it
- [x] **Issue & Return** — the counter loop as one screen with one box
- [x] **Members** — the roll, one person at a time: what they hold, what they
      owe (including a fine still accruing on a late book), and the no-dues
      position. Enrol and edit in their own window.
- [x] **Lending Rules** — the member categories. Every loan period, borrowing
      limit, renewal allowance and fine rate in the library is on this screen,
      read back as a sentence as it is edited.
- [x] **One book** — opened from the catalogue: the bibliographic record, every
      copy of it, accessioning a batch onto the register, and the notes appended
      against a copy. Numbers are allocated sequentially and gap-free under
      concurrency.
- [x] The printed-document house style — the letterhead with the unit's crest,
      the ruled table, the signature block, and a foot that says page x of y
- [x] **Accession Register** — the statutory ledger on screen and as a PDF, over
      a range or whole. 1,555 entries render in under two seconds.
- [x] **Labels** — pocket and spine, on a sheet for an ordinary printer or as
      ZPL for a Zebra. Code 128 is drawn rather than fetched, so the bars stay
      vector at any size; the QR comes from QRCoder. Sizes come from the
      settings, and picking is by ticking because labelling is a batch job.
- [x] **Reports** — overdue, member activity, holdings, copies by state, most
      borrowed and classified holdings. One screen and one document for all six.
      Every one is limited to the viewer's clearance, and producing the
      classified one is written to the activity log before the rows are read.
- [x] **Stock Check** — the scanning loop, a count that survives being
      interrupted over days, reconciliation against the register as it stands at
      close, and the signed shortage statement for the board. It writes nothing
      off: declaring a missing book lost is the board's decision.
- [x] **Withdrawals** — the condemnation register, where that board decision is
      recorded. Batch condemnation under one set of proceedings, the certificate
      of condemnation, and the whole register. A book on loan can only leave as
      lost, and the loss is charged to whoever had it.
- [x] **Reservations** — the hold shelf and the queues, as two lists because
      they are two different jobs. A hold is on the title, never a copy.
- [x] **Fines** — what is owed, what was paid against which receipt, what was
      waived and why. A record, not a cash book: no money is handled anywhere.
- [x] **Settings** — what the unit is called, how it numbers its books, how big
      its labels are, and which screens it has. Four saves, not one, because
      they are four decisions taken at four different times. The accession
      starting number is shown locked, with the reason, once anything is on the
      register.
- [x] **Staff Accounts** — add, edit, suspend, reinstate, and set somebody's
      password. All four lockout guards enforced in `Staff` and explained on the
      form: the field is greyed with the reason beside it before anybody types
      into it. A reset asks the acting administrator for their own password.
- [x] **Activity** — the audit log, newest first, filtered by kind, by whom and
      by date, a page at a time. One history for both programs: an entry written
      by the web application reads the same as one written here. Nothing on the
      screen, or anywhere in this application, can edit or remove an entry.
- [x] **Labels — ZPL** — no longer waiting on the hardware. Every dimension is
      worked out from the stock size on the Settings screen and the printer's
      resolution, in millimetres, so changing the stock changes the labels
      without anybody editing code; 203 and 300 dpi both produce the same
      physical label. A **calibration label** goes at the front of every ZPL
      file: a 10 mm square drawn from the same arithmetic, with the numbers it
      came from printed beside it. Hold a ruler against it — if it measures
      10 mm the settings match the stock, and everything else follows. That is
      a five-minute check on site rather than a code change per unit.
- [x] **Packaging** — `tools/publish.ps1` stages the shipped copy: one
      self-contained 58 MB executable, `data/` beside it, and the README a unit
      reads first. Four files in all. The script warns if loose DLLs appear
      beside the executable, because a single-file build that has quietly
      stopped being one is worth catching here rather than on a client's
      machine.
- [x] **OPAC kiosk** — the reading-room terminal, as a full-screen window with
      no chrome that cannot be closed without a staff password. Anonymous search
      runs at UNCLASSIFIED and there is no box to type a clearance into; the
      clearance comes from the pass in somebody's hand. Scanning a pass — the
      same token the counter resolves on — shows that person their own loans,
      due dates, holds and dues, and nobody else's. It forgets them after two
      minutes, because a member who walks away must not leave their account on a
      screen in a public room.
- [x] **Licence** — hardware-locked activation and the trial banner. The key
      algorithm is an exact port, so a key a unit already holds for the web
      application activates this one unchanged — confirmed against the real key
      on the development machine. The hardware fingerprint asks Windows the
      same three questions in the same order and is never written to disk. The
      banner appears above every screen while on trial, and for a licence only
      once it is within thirty days, so that when it does appear somebody reads
      it.
- [x] **Database** — where the records are, and copies of them. The connection
      is tried before it can be saved, because it is the one setting that can
      stop the application opening and putting it right needs the application.
      Copies are taken with SQLite's own `VACUUM INTO` rather than by copying
      the file: checkpoint-then-copy is nearly safe, and nearly is not good
      enough for the only thing standing between a unit and losing its library.
      A restore keeps today's records aside first, without being asked, and
      clears the write-ahead log so the old one cannot be replayed over the new
      file. On a server the screen says the backups are the server's job rather
      than offering a button that produces something that only looks like one.
- [x] **Member passes** — the card a member carries, at CR80 (85.6 × 54 mm) so
      it fits a laminating pouch bought anywhere, eight to an A4 page with a cut
      guide round each. Photograph or initials, name, membership and personnel
      numbers, unit, expiry, the classification across the foot, and a QR of the
      member's scan token — the value the counter resolves on, and the one that
      dies when a pass is reissued. Printed one at a time or for everybody the
      search is showing, because enrolment happens in intakes.
- [x] **Subjects** — the heading tree the catalogue files under: add, rename,
      move, remove. A heading cannot end up beneath itself, and the way that is
      kept is by leaving the move off the list rather than refusing it
      afterwards. Each heading says what is filed directly under it and what is
      filed anywhere below.
- [x] **Cataloguing** — describing a work: the title page, the people on it in
      the order they are printed, the imprint, where it stands and what it is
      about, with a cover. Authors and publishers are found by name or created,
      so the two applications grow one authority list. The record reads back as
      a catalogue card as it is typed. Saving catalogues the work and nothing
      else — it lands on the book's own screen, where copies are accessioned.

### Sold to more than one unit

Three pieces of work, done together, because they are one question: what does a
second buyer have to change, and how much of it can they change themselves?

**The colour is theirs, and it moves at once.** One settings row drives the
whole window. `Theming` derives the hover, the bright, the soft tint behind a
selected row, the focus ring and the bloom from that one colour, so a unit picks
one thing and the rest follows — a unit asked to choose a hover shade would
choose one that went with nothing. Eight ready-made colours sit under the box
for typing one, because a buyer who knows their arm's colour by sight does not
know it as six hex characters, and asking them to find out is asking them to
leave the application to answer a question about the application. Saving
repaints the window there and then rather than at the next sign-in: somebody
choosing a colour by eye should be looking at the result while they choose.

One thing needed care. The accent is used as *writing* on the near-black bar at
the top, and a red reads there while a navy or a forest green does not — the
rank under somebody's name turned into a smudge the moment the colour changed.
So `AccentOnDark` walks the colour towards white until it measures 4.5:1 against
that bar and uses that instead. A unit whose colour is a deep navy still gets
navy, just a navy that can be read where it is being read.

**A unit's branding cannot be lost.** See `MilLib.BrandProof` above. Two things
were wrong and both were silent: an unreadable colour was stored as the house
red, and the settings form started with "dark window" and "crest in a circle"
already ticked, so a save that ran before the read finished told the unit it
wanted both. The form now refuses to save until it has actually read the
settings, and a colour that cannot be read leaves the stored one alone.

**The records can live on a server.** SQLite, MariaDB and now PostgreSQL, chosen
from one list rather than a checkbox and then a second question underneath it.
The port follows the choice, because nobody remembers 5432 and a stale 3306 left
behind fails with a timeout instead of an explanation. Two faults were fixed on
the way: `ServerVersion.AutoDetect` was being called on every context, which is
a round trip before every screen and — worse — meant the application could not
so much as describe its own tables while the server was down, including on the
one screen that exists to point it somewhere else. And Npgsql's modern timestamp
handling would have slid every due date, issue date and fine by the machine's
offset the first time a unit moved onto PostgreSQL, because the PHP schema
stores a due date as the date it is and nothing more.

### Going through the screens against the web application

The shell was reorganised — five menu buttons in place of an eighteen-row rail,
with the groups the web application uses. What follows is going through the
screens one at a time against the web application, merging the buttons that do
related things and settling the wording and column order page by page.

- **Issue & Return — done.** The web counter is one smart-scan box that reads a
  book's status and picks issue or return for you; the desktop screen already
  worked that way, so this pass was about what the operator sees around the box
  rather than the box itself. The member panel now states the three things that
  decide whether the next scan goes through — what they are cleared for (the
  classification pill, in its conventional marking colours, from the same
  `Band()` the passes and labels use), how much of their allowance is spent
  ("3 of 4 out", the same two numbers the policy enforces), and what they still
  owe in unsettled fines — above the books rather than discovered in a refusal.
  The return panel says what condition the book went out in beside the box, so
  agreeing with the pre-chosen answer means something, and warns that a return
  is about to be flagged as damaged *before* the button is pressed rather than
  after. The sub-unit box shows only for a unit publication, which is the only
  kind that goes to a sub-unit. `MilLib.CounterProof` gained a block proving the
  panel's three claims equal what the rules enforce, and that the damage warning
  fires on exactly the returns the core flags. `MilLib.ViewShot` (new) renders
  the screen headlessly for a look without a login.

- **Books in Library — done.** The list already worked as one search box over a
  virtualised fourteen-hundred-row table; this pass brought it level with the web
  index. A **unit-publication** mark now sits on the row beside the classification
  mark — a unit's own publication is handled differently everywhere it appears, so
  it earns a mark where the eye first meets it. The subline under a title carries
  the web app's material·publisher·year imprint rather than the publisher alone.
  The single search box now also reaches **language and material**, which the web
  app offered as two separate dropdowns — the same reach with one fewer control,
  so "hindi" or "map" narrows the list without a second box. On a book's own page,
  a unit publication now states which **amendment** it is current to, beside the
  mark that it is a controlled publication — the register is the only place that
  difference is kept. `MilLib.ViewShot` renders the real 1,397-book catalogue and
  a book's page; because the imported library holds nothing classified and no unit
  material, the two marks are shown against a pair of staged rows on top of the
  real list.

- **Members — done.** The screen was already a master-detail the web app does
  not have — the roll on the left, one person on the right, with what they hold,
  what they owe, and whether they can be signed off all in the panel rather than
  down a separate page. This pass closed the parity gaps. The **pass photo** now
  shows beside the name in the panel, as it does on the web show page; a member
  without one simply starts at their name rather than showing an empty square.
  The **security deposit** (the unit's money, held against the pass and returned
  at sign-off) and any **remarks** join the panel, each shown only when there is
  one. The single search box already reached **category**, so the web app's
  category dropdown needs no separate control here. `Workspace.PhotoPath` resolves
  a member's photograph the same way a book cover is resolved. `MilLib.ViewShot`
  renders the screen against the real member.

  Worth flagging, not code: the desktop `app/data` folder carries only the
  database and the crest — **the member photos and book covers were not brought
  across** when the library was converted to a single data folder. They still sit
  in the web application's `storage/app/public`. The resolver finds a photo or a
  cover whenever the file is beside the data (which is why the shot copies them
  in), so shipping a unit its faces and covers is a matter of copying those two
  folders next to `database.sqlite`, not of more code.

- **Reports — done.** The screen was already ahead of the web app in shape — a
  described list of reports on the left, the report itself on the right with its
  filters above the table, and every report exportable to PDF or spreadsheet from
  the same query, all of it gated to the viewer's clearance and none of it
  turn-off-able. The one thing the flat list did not carry was the web index's
  two **headings**: the reports now sit under **Circulation** and **Catalogue**,
  in that order, so somebody moving between the two applications finds a report
  where they left it. `Reports.Section` (core) decides which heading a report
  belongs under, shared by both so they cannot drift. The classified report keeps
  its "Recorded" mark, and the clearance limit is stated once at the foot of the
  list rather than on every report. `MilLib.ViewShot` renders it over the real
  catalogue.

  Note on placement: the web app lists the **Accession Register** as a report
  card; the desktop gives the statutory ledger its own top-level screen instead,
  which is why it is not in this list.

- **Packaging — done in `publish.ps1`.** Following on from the Members pass, the
  publish script now stages `data\member-photos` and `data\covers` on every
  build — from `app\data` if the folders are there, otherwise from the web
  application's `storage\app\public` — so a shipped unit carries its faces and
  covers rather than a screen full of blanks. It warns if it can find neither.

- **Administration group — done, mostly by already being done.** The group holds
  Reports (grouped this round), Lending Rules, Staff Accounts, the Activity Log,
  Settings, Database & Backups and Licence. Settings/Database/Licence were built
  in the white-label and database passes. The rest were checked screen by screen
  against the web application and already stand at or beyond it, so this pass was
  verification rather than change:

  - **Lending Rules** (member categories) — the web app is an eleven-column
    table; the desktop is a master-detail where every loan rule is editable
    policy, grouped as "how much and for how long" and "what else they may do",
    with a plain-words summary of the whole rule and the count of members each
    governs. Nothing about lending is fixed in the software, which is the one
    rule that matters here.
  - **Staff Accounts** — roles, the clearance ceiling, last-signed-in, the "You"
    mark on the current account, the lockout guards, and a password reset that
    demands the administrator's own password. Accounts are suspended, never
    deleted, so the audit trail keeps every name. Beyond the web index/edit pair.
  - **Activity Log** — a plain-English audit trail ("Took a book back — damaged:
    yes, from: NEW, to: POOR"), filtered by kind, person and date, with the
    security-notable entries in red and a footer stating nothing on it can be
    edited or removed by anybody in either program. The web application has no
    such screen at all.

  `MilLib.ViewShot` renders all three against the real data.

- **Dashboard (Home) — done.** Brought level with the web operations console. A
  role · clearance · date line under the greeting; the figures are built from
  the view model and gated by ability and feature, so a counter clerk without
  reservations or fines sees a shorter row rather than empty cards — with Due
  back today, Holds ready and Unpaid fines added to the six it had. Every figure
  is a card that is also a way in: clicking it walks to the screen it is about.
  The trend charts remain out; the desktop has no charting stack.

- **Fines — done.** Already had the status filter, member search, per-row
  pay/waive with a re-check at settle, the settled outcome, and the library's
  outstanding balance. Added the web table's Days column: an overdue charge shows
  "N days late" under its amount.

- **Reservations — done.** Already beyond the web app (it can place a hold right
  here, which the web app only does from a title page), with expiry and urgency
  on ready holds and queue position on waiting ones. Added the web ready-list's
  Copy column: a ready hold shows the accession of the copy set aside, so it can
  be found on the hold shelf.

- **Accession Register — done.** The full fourteen-column statutory ledger was
  already in print (landscape, letterhead, page footers) — beyond the web
  register. Added the ledger-book column to the on-screen summary, which the
  imported register uses on every row.

- **Subjects — done, by already being done.** A subject tree with cycle-safe
  reparenting, per-heading filing counts, and the books filed under each, against
  the web app's flat name/parent table. No change needed.

- **Labels & Barcodes — done, by already being done.** A standalone label
  station: search plus range selection for a fresh intake, pocket/spine, a
  Barcode / QR / Both choice, and two outputs — a PDF sheet for any printer and
  ZPL for a Zebra. The web app prints only from a title page. No change needed.

- **Stock Check — done, by already being done.** The whole verification workflow
  — start, scan the shelves with running found / expected / not-in-register /
  scanned-twice counts, close with a board reference, abandon, reconcile, print
  the shortage — in one master-detail screen rather than the web app's separate
  pages. No change needed.

- **Withdrawals — done.** The condemnation register was mostly complete (board
  batches, reasons, sanction fields, loss amounts, taking copies a stock check
  reported missing, certificate and register printing). SUPERSEDED was a dead
  option, though: the core requires the title that replaces a superseded book and
  the screen gave no way to name it, so it always failed. Added a "Replaced by"
  field for that reason alone, which the core records as the succession.

- **OPAC / Kiosk — done, by already being done.** The web app's per-member
  self-service login is reimagined as a locked-down reading-room terminal:
  anonymous catalogue search at unclassified, scan-your-pass to see your own
  loans, holds and fines (the pass is the identification, so no per-member
  password), a clearance line, place-a-hold, a short idle timeout that forgets
  whoever scanned in, and a staff-password exit. A better fit for a shared
  air-gapped terminal than a web login.

### Fixes found while going through the screens

- **Covers and member photos did not show** because the conversion to a single
  data folder never brought the picture files across — and the one book with a
  cover pointed at a file that lives in the web app's `public/storage`, a
  separate folder from `storage/app/public`. The resolver was right; the files
  were missing. They are now copied into `app/data`, `publish.ps1` stages them
  from `public/storage` on every build, and `Workspace.PhotoPath` resolves a
  member photo the way a cover is resolved.
- **The pass went straight to a PDF in an external viewer.** It is now shown in
  the application first — `PassDocument` gained a single-card mode and
  `PassPreview` renders that card to an image (the print document itself, so
  screen and paper cannot drift) with Print / Save / Close.
- **The licensing salt was in committed source.** It now lives in a git-ignored
  `LicenceSecret.cs`, with a committed stand-in (`LicenceSecret.Default.cs`) so a
  public clone compiles and runs without minting real keys. See the licence-salt
  note below.

That leaves no screen outstanding. What remains is not code:

- **Hold the calibration label against a ruler.** One label off the roll settles
  whether the stock sizes on the Settings screen match the stock actually
  loaded. Everything else about the thermal labels follows from that, and it
  cannot be done without the printer in the room.
- **Decide which application is the real one.** Both read the same schema but
  they are separate copies of it. While both exist, work in one.

## Notes for whoever picks this up

- **The repository is public** at `github.com/anil7244/mil-lib-net`, with the
  licence salt and every unit's records kept out of it (see `.gitignore` and the
  licence-salt note). A private `mil-lib-net-history` holds the earlier history
  from before the salt was externalised, and can be deleted once nothing needs
  it. A unit's real data never goes to the repository.
- **The two applications must agree.** The ability matrix, the bcrypt cost, the
  loan rules and the accession numbering are all duplicated here on purpose. Any
  change to one side is a change to both, and `MilLib.DataProof` is where that
  gets checked.
- **Loan rules live in `member_categories`,** never in code. No screen may
  hardcode a loan period, a book limit or a fine rate.
- **The licence salt.** The web application keeps `LICENSE_SECRET_SALT` in
  `.env`. A single-file executable has nowhere comparable to put it — whatever
  it is compiled with can be read out of the binary by anybody who cares to.
  That is not worse than a readable `.env`; it stops casual copying, not a
  determined person with a disassembler. What it must not do is sit in source a
  public repository can carry, so the real value lives in a **git-ignored
  `src/MilLib.Desktop/Services/LicenceSecret.cs`**, compiled in when present. A
  committed stand-in (`LicenceSecret.Default.cs`) is left out of the build when
  the real file exists (see the `.csproj`), so a public clone compiles and runs
  but mints worthless keys — only the vendor's own build is licensable. The
  proof tool takes the salt from `MILLIB_LICENCE_SALT` and skips its
  cross-compatibility checks when it is not set. To build a licensable copy,
  create `LicenceSecret.cs` with the real salt.
- **This library's catalogue has no authority records at all.** The import came
  from a stock ledger, so `authors`, `publishers`, `categories` and both pivot
  tables are empty — 1,397 titles with no author against any of them. The
  cataloguing form is how they get filled in, one book at a time, and the
  Subjects screen is what will make the subject list worth ticking.
- **Pictures live beside the data file** — `crest.png`, and covers and member
  photos under the path the database records. The web application serves these
  from `public/storage` (a folder distinct from `storage/app/public` on this
  install), which is where the database paths resolve, so that is where
  `publish.ps1` and the master `app/data` folder take them from. A build that
  omits them shows a blank for every face and cover.

## Three things that were got wrong once

Written down because each was invisible until something was actually driven,
and each would come back the same way in the next screen.

- **A write path must not leave rows attached.** Reads on this connection are
  untracked, so anything still tracked after a save is a row a later operation
  will find a second copy of — and EF refuses, with an error about identity that
  says nothing about the book somebody is holding. Every write goes through
  `SaveAndForgetAsync`, never `SaveChangesAsync`.
- **A typed value must reach the view model as it is typed.** Bound the default
  way, a box commits when it loses focus — and a box the operator submits with
  Enter never does. Every box a person types into and submits without leaving
  carries `UpdateSourceTrigger=PropertyChanged`.
- **Enter has to be bound to something.** A barcode scanner is a keyboard that
  types very fast and presses Enter. On a screen with no button for Enter to
  reach, the scan lands in the box and nothing happens — which is exactly what
  the counter did the first time it was tried. The scan box binds Enter itself.
- **A list pane is narrower than it looks.** Two screens shipped with a
  star-width name column crushed to nothing by the fixed columns beside it,
  because the detail panel takes 440px off the width first. Count the fixed
  columns against the pane, not the window; anything that will not fit belongs
  on a second line under the name.
- **A dropdown speaks to a person.** Bound straight to an enum, a list offers
  GOOD, DAMAGED and TOP_SECRET. Every one of them goes through
  `Spoken` / `Words.Any` — which has to dispatch on the runtime type, because
  the compiler picks the general overload for a boxed value and turns
  "Top Secret" into "Top secret".
- **`CalendarDatePicker.SelectedDate` is `DateTime?`.** Bound to a
  `DateTimeOffset`, it throws at run time and Avalonia prints the cast exception
  in full where the field should be. Two forms shipped that way, and one of them
  only showed it below the fold. Date properties on a view model are `DateTime?`.
- **Do not trust an entity you were handed across a write.** Offering a returned
  copy to the queue expired the stale holds first — and expiring one releases
  the copy it was holding, which could be that very copy. The object passed in
  still said "reserved", so the next person in the queue was never offered the
  book: it sat on the shelf while three people waited, and nothing said so.
  Re-read the state after anything that may have changed it.
- **A disabled control must look disabled.** Only the `withIcon` buttons had a
  disabled state, so a plain primary button — "Withdraw them", "Take it back",
  "Save" — sat in full crimson and did nothing when clicked. A control that
  looks live and is not teaches people the application is broken. There is now a
  `Button:disabled` rule covering all of them.
- **Raise the change last.** Twice now a screen has redrawn from a collection
  that had not been filled yet: the reports table kept the previous report's
  columns because `Report` was assigned before its rows were loaded. Fill what
  the screen reads from, *then* raise the property it watches.
- **A view cannot subscribe to something that already happened.** The labels
  screen counted its ticks by having the view listen to the rows as they were
  added — and the rows are loaded from the view model's constructor, so they
  were all in place before the view existed. Ticking three left the count
  saying none and both buttons greyed out. Whatever owns a collection should
  listen to it.
- **A SQLite database is more than one file.** Copying `database.sqlite` over a
  working copy without its `-wal` and `-shm` leaves the old write-ahead log to
  replay over the new file, so the reset silently does nothing. Delete the whole
  set, then copy.
- **A horizontal StackPanel does not respect its column.** It gives its first
  child all the width that child asks for and pushes the rest out — so a long
  name carried the badge beside it clean over the next column, and the line
  under it was cut off mid-character reading "signed in today at 12:2". Where a
  name shares a cell with anything, use a `Grid` of `*,Auto` and let the name
  trim.
- **The reload after an action must not wipe what the action said.** Every
  write on these screens ends by re-reading the list, which reselects the row
  and reopens the editor — and the editor was clearing the outcome banner as it
  opened. A refused suspension appeared to do absolutely nothing. Clear the
  banner when an action *starts*, never when a form opens.
- **One thing, one name.** The role dropdown offered "Superadmin" while the
  column beside it said "Super Administrator", because `Words.Of` and
  `Abilities.Label` each spelt the roles out separately. `Words.Of(UserRole)`
  now defers to `Abilities.Label`. Two names for one thing on one screen reads
  as two things.
- **A prefix match is not a category.** Marking the activity log's notable rows
  with `Action.StartsWith("USER_")` caught `user_login` as well, so every row on
  the screen was red — and a column where everything is marked is a column
  nobody reads the marks in. The watched actions are named one at a time.
- **A library can bring its own furniture.** QuestPDF stages eighteen Lato
  font files — eleven megabytes — beside the executable as its default
  typeface. Every document here names Calibri or Consolas, so none was ever
  opened; what they did was make a liar of a README saying the .exe is the
  whole application. Dropped from the publish, and then *proved* by printing a
  document from the staged build with the folder gone.
- **PHP's `in_array` compares loosely, and something depended on it.** This
  machine reports a motherboard serial of `00000000`. The web application
  rejects it — not by listing it, but because `'00000000' == '0'` is true
  numerically — and falls through to the BIOS serial. An exact string match
  here accepted it, and the desktop application worked out a **different**
  hardware ID from the same machine, which would have made every licence key a
  unit already holds fail on the day they installed it. Found by asking both
  programs for the ID and comparing. When porting a check, port what it *does*,
  not what it appears to say.
- **A viewer holds the file it opened.** Printing a pass, looking at it,
  correcting a rank and printing again failed every time with "the process
  cannot access the file" — the PDF reader still had the first one. Every
  document goes to a readable name in one folder, so they all had it. The name
  is now probed and falls back to "… (2).pdf" rather than the write failing.
  Found by reading `errors.log` after driving, not by anything on screen.
- **A derived property is only as fresh as what it is raised from.** "Remove"
  stayed on the form for a brand-new subject heading, because `MayRemove` is
  raised off `Editing` — and `Editing` was already true, so setting it again
  raised nothing. When a screen changes what it is editing without changing
  whether it is editing, every derived property has to be raised by hand.
- **Assigning the value already there raises nothing.** The "filed under" box
  came up blank, because rebuilding its list emptied it and the value then
  assigned was equal to the one the property already held. Clear the property
  first when the list under it has been rebuilt.
- **An empty value still goes through the formatter.** A work with no copies
  showed "JAKLI/" in the accession column, because the prefix was prepended to
  an empty number — which reads as a number somebody has lost the end of rather
  than as a book that is not on the register yet. Format nothing as nothing.
- **The general rule lower-cases an acronym.** `Words.Of(Enum)` sentence-cases,
  so the classification scheme every book in the library is filed under offered
  itself as "Ddc". Third time this has bitten (TOP_SECRET, MaterialType, now
  this): an enum whose members are not ordinary words needs a named overload
  **and** a line in `Words.Any`.
- **A count must be of the same set as the list.** The activity tally counted
  the whole table while the list was filtered, so a filtered page read "1–11 of
  20" — true by coincidence there and wrong the moment there is a second page.
  The filters live in one method that both the page and the count go through.
