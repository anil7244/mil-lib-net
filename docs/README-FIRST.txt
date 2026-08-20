MILITARY LIBRARY MANAGEMENT SYSTEM - DESKTOP APPLICATION
========================================================

WHAT THIS IS
  The unit library, as a native Windows program. No web server, no browser,
  no port, no PHP. One file runs it.

TO RUN
  Double-click "Library Manager.exe" and sign in.

  Use the same username and password as the web application - they are the
  same accounts, in the same records. Signing out returns you to the sign-in
  screen; closing the window closes the application and signs you out.

  Nothing is installed and nothing runs in the background.

  NOTE: signing in gates the application, not the file. The records sit in
  data\database.sqlite, which anyone holding this folder can open with other
  tools. Keep the folder as safe as the records deserve.

WHAT THE PC NEEDS
  64-bit Windows 10 or later. Nothing else.

  There is no installer and nothing to install. Everything the application
  needs - including the .NET runtime it is built on - is inside the one .exe.
  Copy the folder to the other PC and double-click it.

  It does not need .NET installed, or PHP, or XAMPP, or a database server, or
  administrator rights. It writes only inside its own folder.

  Two things to expect the first time, neither of them a fault:
    - Windows may show "Windows protected your PC". Click "More info" then
      "Run anyway". This is SmartScreen, and it says that about any program
      that has not been bought a code-signing certificate.
    - Some antivirus scanners dislike a single-file application unpacking
      itself on first run. Allow the folder if asked.

WHAT IS INSIDE
  Library Manager.exe       the application, complete
  data\database.sqlite      the library - every book, member and loan
  data\crest.png            the unit crest, used on screen and on documents
  data\backups\             copies of the library, kept automatically
  data\errors.log           written only if something goes wrong

  If the crest is missing, everything simply prints without it.

YOUR DATA
  Everything lives in data\database.sqlite - the catalogue, the accession
  register, the members, every loan, fine, hold and stock check.

  To back up:  the Database screen, "Take a copy now". It writes a complete
               copy into data\backups and is safe to do while the library is
               being used.
  To restore:  the Database screen, pick a copy, "Put the chosen copy back".
               Today's records are kept aside first, so it can be undone.
  To move:     copy the whole folder to the other PC.

  Turn on "Keep copies automatically" and it takes one at sign-in whenever the
  last is older than the interval you set. Do that on the first day.

  The application looks for the data file next to itself first, so a folder
  handed to somebody carries its own records and can never open anybody
  else's by accident.

  IMPORTANT: this is a SEPARATE COPY from the one the PHP application uses.
  Changes made here do not appear there, and the other way round. That is
  deliberate while both exist - keep using one of them as the real one.

THE LICENCE
  The application runs for 14 days from the first time it is opened, with
  everything working. After that it needs a licence key.

  The key is issued against this machine and works on no other. The Licence
  screen shows the hardware ID and a block of text to send; the key that comes
  back is typed into the box on the same screen.

  A key already issued for the web application on this machine works here
  unchanged - it is the same scheme and the same hardware ID.

  A licence controls whether the application opens. It has nothing to do with
  the records: if it lapses, every book, member and loan stays exactly where
  it is, and entering a key puts everything back.

WHAT IT DOES
  Home             what the library holds, what is out, and what is overdue
  Books in Library the catalogue; catalogue a new book, correct a record, and
                   open a book to see and accession its copies
  Accession Reg    the statutory register, on screen as it prints; nothing on
                   it can be edited - a correction is a note against the copy
  Subjects         the heading tree books are filed under
  Labels           barcode and QR labels for the books, on A4 sheets or to a
                   label printer
  Issue & Return   one box: scan a book or a pass and it works out which
  Members          the roll, the no-dues chit, and the printed pass
  Reservations     the queue for a book that is out
  Fines            what is owed, taking payment, and waiving with a reason
  Stock Check      counting the shelves against the register
  Withdrawals      the condemnation register and its certificate
  Reports          six reports, and the classified one behind a clearance gate
  Lending Rules    what each kind of member may borrow, for how long, and what
                   a late day costs - nothing about lending is fixed in the
                   software
  Staff Accounts   who may sign in, what they may do, and setting a password
  Activity         what has been done and by whom, newest first
  Settings         the unit's name and crest, accession numbering, label sizes,
                   and which screens this library has at all
  Database         where the records are, and copies of them
  Licence          the hardware ID, and the box to type a key into

  Nothing on the Activity screen or in the accession register can be edited or
  deleted from here. A record of what happened is only worth having if it
  cannot be tidied up afterwards.

  A password set on the Staff Accounts screen is hashed exactly as the PHP
  application hashes it - bcrypt, cost 12 - so it works there on the very next
  sign-in.

THE READING ROOM
  "Reading room" at the bottom of the menu turns this machine into a public
  catalogue terminal: full screen, no menu, and no way back into the library
  system without a member of staff's password.

  A member can search the catalogue and see whether a book is on the shelf.
  Scanning their own pass shows them their own loans, due dates, holds and
  dues - and nobody else's. It clears itself after two minutes so nobody's
  account is left on a screen in a public room.

  Until somebody scans a pass the terminal shows the ordinary catalogue only.
  Classified material never appears on it, and there is no way to ask it to.

PRINTING
  The register, the reports, the labels, the passes and the condemnation
  certificate all produce a PDF. Some open it to look at; some ask where to
  save it. Nothing is sent anywhere - the file is yours.

  Member passes print at 85.6 x 54 mm, the size of a bank card, eight to an A4
  page with a line to cut along. Any laminating pouch fits them.

IF SOMETHING GOES WRONG
  - The data file is missing: the application says so on its own screen and
    names the file it was looking for. Nothing is lost; put the file back, or
    restore a copy from data\backups.
  - Anything else: data\errors.log records what happened and when. Send that
    file when reporting a problem.
  - Antivirus quarantining the .exe: allow the folder. A single-file
    application unpacks itself on first run, which some scanners dislike.

FOR SUPPORT
  Tactical Code
  Telephone  +91 96433 25206
  Email      anil7244@gmail.com
  Online     www.tacticalcode.in
  Where      Samba, J&K, India

  Quote the hardware ID from the Licence screen when asking for a key.
