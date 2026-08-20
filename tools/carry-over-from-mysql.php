<?php

/**
 * Carries the library out of MySQL and into the file the desktop application
 * reads.
 *
 * The schema on the receiving side is not written here. It is built by running
 * the PHP application's own migrations against the file first, so the two
 * cannot drift:
 *
 *   cd D:\xampp\htdocs\mil-lib
 *   set DB_CONNECTION=sqlite
 *   set DB_DATABASE=D:\mil-lib-net\app\data\database.sqlite
 *   D:\php84\php.exe artisan migrate --force
 *
 * Then this copies the rows. It replaces whatever is in the receiving file —
 * it does not merge, and there is no sensible way it could: two libraries that
 * have both been worked in have two different ideas of which book is out.
 *
 *   D:\php84\php.exe tools\carry-over-from-mysql.php
 *
 * Run it again whenever the changeover is rehearsed. It is safe to repeat.
 */

$source = [
    'dsn'  => 'mysql:host=127.0.0.1;port=3306;dbname=mil_lib;charset=utf8mb4',
    'user' => 'root',
    'pass' => 'password@123',
];

$target = __DIR__ . '/../app/data/database.sqlite';

/**
 * Parents before children, so that a file opened with foreign keys enforced
 * reads as sound even though this copy does not enforce them itself.
 *
 * Laravel's own tables — migrations, cache, jobs, sessions — are deliberately
 * absent. They describe a web application at a moment in time and mean nothing
 * to a program with no web server, no queue and no browser session.
 */
$tables = [
    'branches',
    'publishers',
    'authors',
    'categories',
    'users',
    'withdrawals',
    'titles',
    'title_author',
    'title_category',
    'copies',
    'copy_annotations',
    'member_categories',
    'members',
    'member_cards',
    'loans',
    'renewals',
    'reservations',
    'fines',
    'stock_verifications',
    'stock_verification_scans',
    'settings',
    'audit_log',
    'accession_counters',
    'license_info',
];

if (! file_exists($target)) {
    fwrite(STDERR, "There is no file at {$target}.\n");
    fwrite(STDERR, "Run the migrations against it first — see the top of this file.\n");
    exit(1);
}

$mysql = new PDO($source['dsn'], $source['user'], $source['pass'], [
    PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
]);

$sqlite = new PDO('sqlite:' . $target, null, null, [
    PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
]);

$sqlite->exec('PRAGMA foreign_keys = OFF');
$sqlite->exec('PRAGMA journal_mode = WAL');

$carried = 0;

foreach ($tables as $table) {
    $columns = columnsOf($sqlite, $table);

    if ($columns === []) {
        printf("%-28s skipped — not in the receiving file\n", $table);
        continue;
    }

    $sqlite->exec("DELETE FROM `{$table}`");

    $rows = $mysql->query("SELECT * FROM `{$table}`");

    $list = '`' . implode('`,`', $columns) . '`';
    $marks = implode(',', array_fill(0, count($columns), '?'));

    $insert = $sqlite->prepare("INSERT INTO `{$table}` ({$list}) VALUES ({$marks})");

    $count = 0;

    $sqlite->beginTransaction();

    while ($row = $rows->fetch(PDO::FETCH_ASSOC)) {
        $values = [];

        foreach ($columns as $column) {
            // A column the receiving side has and the sending side does not is
            // left null rather than guessed at.
            $values[] = normalise($row[$column] ?? null);
        }

        $insert->execute($values);
        $count++;
    }

    $sqlite->commit();

    printf("%-28s %6d\n", $table, $count);

    $carried += $count;
}

// The counters SQLite keeps for AUTOINCREMENT columns are not touched by an
// INSERT that supplies its own key, so without this the next book added would
// be handed an accession row id that is already taken.
foreach ($tables as $table) {
    $sqlite->exec(
        "INSERT OR REPLACE INTO sqlite_sequence (name, seq)
         SELECT '{$table}', (SELECT MAX(rowid) FROM `{$table}`)
         WHERE EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = '{$table}')"
    );
}

printf("\n%d rows carried over into %s\n", $carried, realpath($target));

/** The receiving side decides which columns exist. */
function columnsOf(PDO $sqlite, string $table): array
{
    $rows = $sqlite->query("PRAGMA table_info(`{$table}`)")->fetchAll(PDO::FETCH_ASSOC);

    return array_column($rows, 'name');
}

/**
 * MySQL hands back everything as a string. Most of that is fine — SQLite is
 * happy to hold "1" in an integer column — but a zero date is not a date and
 * must not travel as one.
 */
function normalise(mixed $value): mixed
{
    if ($value === '0000-00-00' || $value === '0000-00-00 00:00:00') {
        return null;
    }

    return $value;
}
