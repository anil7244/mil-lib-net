# mil-lib — desktop

A military unit's library, as a native Windows program. No web server, no
browser, no port, no PHP — one self-contained executable and a folder of records
beside it. It is a port of the Laravel/PHP **mil-lib** system, sharing that
application's schema, lending rules, ability matrix and BCrypt password hashes,
so a unit can run either against the same records.

It is built the way **TCE Licence and Billing Manager** is built — same
framework, same shape, same design system — so a person who maintains one can
maintain the other.

## What it is made of

| | |
|---|---|
| Framework | .NET 10 + Avalonia 12 (Fluent), MVVM via CommunityToolkit.Mvvm |
| Data | EF Core 9 over **SQLite**, with **MariaDB/MySQL** and **PostgreSQL** as options on the Database screen |
| Documents | QuestPDF, QRCoder, SkiaSharp (passes, spine/pocket labels, reports, ZPL for Zebra printers) |
| Passwords | BCrypt, cost 12 — the same hash the PHP application writes |
| Shipped as | one self-contained single-file `.exe`, no runtime to install |

## Layout

```
src/MilLib.Core        the records and the rules — knows nothing about a screen
src/MilLib.Desktop     the windows — views, view models, machine services
tools/MilLib.*Proof    console "proof" tools that check the rules against real
                       data (counter, catalogue, licensing, servers, branding…)
tools/MilLib.ViewShot  renders screens headlessly, for a look without a login
tools/publish.ps1      builds the shipped single-file copy + its data folder
docs/PORT-PLAN.md      the detailed design record and screen-by-screen status
docs/README-FIRST.txt  the handover note that ships beside the executable
```

The two-level catalogue model is deliberate and load-bearing: **titles** (the
bibliographic work) → **copies** (the physical objects, one accession number and
barcode each). There is no `quantity` on a title — how many there are is answered
by counting copies. Loan rules (`max_books`, `loan_days`, `max_renewals`,
`fine_per_day`, grace, clearance ceiling) live in member-category rows, never in
code.

## Building

Requires the **.NET 10 SDK**. Earlier SDKs cannot build it.

```bash
dotnet build MilLib.sln
```

Run a proof tool (each works on a throwaway copy of the library, never the real
records):

```bash
dotnet run --project tools/MilLib.CounterProof
```

## Shipping a unit

`tools/publish.ps1` produces a self-contained folder — the executable, the
database, the unit crest, member photos and book covers — ready to hand over:

```powershell
powershell -File tools/publish.ps1 -Fresh
```

Copy the folder to the target PC and double-click. Nothing is installed; it
needs only 64-bit Windows 10 or later, and writes only inside its own folder.

## White-label

The application recolours to a unit's own accent at runtime (menubar, controls,
focus rings, and legible-on-dark text derived from one chosen colour), takes the
unit's crest, name and motto, and can point at a file, a MariaDB server or a
PostgreSQL server — so the same build is sold to any unit and set up without a
rebuild.

## A note on data

A unit's real records never enter this repository. `app/data/`, every `*.sqlite`,
and the staged `publish/` handover copy are git-ignored. Signing in gates the
application, not the file: the records sit in `data/database.sqlite`, which
anyone holding the folder can open with other tools — keep the folder as safe as
the records deserve.
