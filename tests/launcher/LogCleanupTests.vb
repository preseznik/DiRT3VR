Imports DiRT3VR
Imports System.IO

Module LogCleanupTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "log-cleanup", name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "log-cleanup-users", name), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Function Id() As String
        Return Guid.NewGuid().ToString("N")
    End Function

    ' A VR session folder as the launcher and renderer leave it, dated `age` ago.
    Private Function Session(ctx As InstallContext, age As TimeSpan, now As DateTime, Optional files As String() = Nothing) As String
        Dim folder = Path.Combine(ctx.UserRoot, "logs", "session-" & Id())
        Directory.CreateDirectory(folder)
        For Each name In If(files, {"session.json", "native-4242.log", "hud-probe-4242-90.rgba", "hud-probe-4242-90.json"})
            File.WriteAllText(Path.Combine(folder, name), name)
            File.SetLastWriteTimeUtc(Path.Combine(folder, name), now - age)
        Next
        Directory.SetLastWriteTimeUtc(folder, now - age)
        Return folder
    End Function

    ' A finished recovery: restored-<id>.json and the backups it names.
    Private Function Restored(ctx As InstallContext, age As TimeSpan, now As DateTime, Optional sharedBackup As String = Nothing) As (Journal As String, Backups As String())
        Dim folder = New OwnedFileTransaction(ctx).Folder
        Directory.CreateDirectory(folder)
        Dim backups = {Id() & ".bin", Id() & ".bin", If(sharedBackup, Id() & ".bin")}
        Dim journal As New RecoveryJournal With {.Schema = 2, .InstallId = ctx.Id, .GraphicsPath = ctx.GraphicsPath, .Entries = New List(Of RecoveryEntry) From {
            New RecoveryEntry With {.Target = "graphics", .Backup = backups(0), .AppliedBackup = backups(1)},
            New RecoveryEntry With {.Target = "proxy", .Backup = backups(2), .AppliedBackup = backups(2)}}}
        Dim journalPath = Path.Combine(folder, "restored-" & Id() & ".json")
        SafeFiles.SaveJson(journalPath, journal)
        For Each name In backups.Append(Path.GetFileName(journalPath))
            Dim file = Path.Combine(folder, name)
            If Not IO.File.Exists(file) Then IO.File.WriteAllText(file, "backup")
            IO.File.SetLastWriteTimeUtc(file, now - age)
        Next
        Return (journalPath, backups.Select(Function(name) Path.Combine(folder, name)).ToArray())
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim now = DateTime.UtcNow
        Dim ctx = Context(root, "sessions")
        Dim old = Enumerable.Range(0, 12).Select(Function(i) Session(ctx, TimeSpan.FromDays(40) + TimeSpan.FromHours(i), now)).ToArray()
        Dim fresh = Session(ctx, TimeSpan.FromHours(1), now)
        Dim foreign = Session(ctx, TimeSpan.FromDays(60), now, {"notes.txt"})
        Dim mixed = Session(ctx, TimeSpan.FromDays(60), now)
        File.WriteAllText(Path.Combine(mixed, "late.txt"), "written yesterday") : File.SetLastWriteTimeUtc(Path.Combine(mixed, "late.txt"), now.AddDays(-1)) : Directory.SetLastWriteTimeUtc(mixed, now.AddDays(-60))
        Dim locked = Session(ctx, TimeSpan.FromDays(70), now)
        Dim other = Path.Combine(ctx.UserRoot, "logs", "fixture") : Directory.CreateDirectory(other) : Directory.SetLastWriteTimeUtc(other, now.AddDays(-90))
        Dim result As LogCleanup.Receipt
        Using holder As New FileStream(Path.Combine(locked, "native-4242.log"), FileMode.Open, FileAccess.Read, FileShare.None)
            result = LogCleanup.Run(ctx, now)
        End Using
        ' Newest 10 by date: fresh plus old(0..8). Beyond them: old(9..11), foreign, mixed, locked.
        check(result.Skipped = "" AndAlso Directory.Exists(fresh) AndAlso old.Take(9).All(Function(folder) Directory.Exists(folder)), "the newest 10 session folders are kept (" & result.Skipped & ")")
        check(old.Skip(9).All(Function(folder) Not Directory.Exists(folder)) AndAlso result.FilesRemoved >= 12, "older session folders, with their F8 captures, are removed (" & result.FilesRemoved & " files)")
        check(Directory.Exists(foreign) AndAlso Directory.Exists(other), "folders that aren't DiRT3VR session logs are left alone")
        check(Directory.Exists(mixed) AndAlso File.Exists(Path.Combine(mixed, "session.json")), "a session folder with a recent file is kept whole")
        check(Directory.Exists(locked) AndAlso File.Exists(Path.Combine(locked, "session.json")), "a session folder with a file in use is kept whole")
        check(File.Exists(LogCleanup.ReceiptPath(ctx)) AndAlso File.ReadAllText(LogCleanup.ReceiptPath(ctx)).Contains("FilesRemoved"), "logs\cleanup.json records what was removed")
        check(LogCleanup.Run(ctx, now.AddHours(2)).Skipped = "Already checked today", "cleanup runs at most once a day")
        check(LogCleanup.Run(ctx, now.AddDays(2)).Skipped = "" AndAlso Not Directory.Exists(locked), "the next day it removes the folder that was in use")

        ' Finished recoveries: journals, their backups and the game's copies.
        ctx = Context(root, "recovery")
        Dim recoveryFolder = New OwnedFileTransaction(ctx).Folder
        Dim sharedName = Id() & ".bin"
        Dim recent = Enumerable.Range(0, 9).Select(Function(i) Restored(ctx, TimeSpan.FromDays(31) + TimeSpan.FromHours(i), now)).ToArray()
        Dim sharing = Restored(ctx, TimeSpan.FromDays(31) + TimeSpan.FromHours(9), now, sharedName)
        Dim expired = Enumerable.Range(0, 3).Select(Function(i) Restored(ctx, TimeSpan.FromDays(45) + TimeSpan.FromHours(i), now, If(i = 0, sharedName, Nothing))).ToArray()
        Dim unreadable = Path.Combine(recoveryFolder, "restored-" & Id() & ".json")
        File.WriteAllText(unreadable, "{ broken") : File.SetLastWriteTimeUtc(unreadable, now.AddDays(-90))
        Dim copies = Enumerable.Range(0, 12).Select(Function(i)
                                                        Dim copy = Path.Combine(recoveryFolder, "game-written-" & Id() & ".xml")
                                                        File.WriteAllText(copy, "<x/>") : File.SetLastWriteTimeUtc(copy, now - TimeSpan.FromDays(40) - TimeSpan.FromHours(i))
                                                        Return copy
                                                    End Function).ToArray()
        Dim orphan = Path.Combine(recoveryFolder, Id() & ".bin") : File.WriteAllText(orphan, "unknown") : File.SetLastWriteTimeUtc(orphan, now.AddDays(-90))
        result = LogCleanup.Run(ctx, now)
        check(result.Skipped = "" AndAlso recent.All(Function(item) File.Exists(item.Journal) AndAlso item.Backups.All(Function(backup) File.Exists(backup))) AndAlso File.Exists(sharing.Journal),
              "the newest 10 recovery records and their backups are kept (" & result.Skipped & ")")
        check(expired.All(Function(item) Not File.Exists(item.Journal)) AndAlso expired.Skip(1).All(Function(item) item.Backups.All(Function(backup) Not File.Exists(backup))), "older recovery records go with the backups only they name")
        check(File.Exists(Path.Combine(recoveryFolder, sharedName)), "a backup that a kept record also names is kept")
        check(File.Exists(unreadable) AndAlso File.Exists(orphan), "unreadable records and backups no record names are left alone")
        check(copies.Take(10).All(Function(copy) File.Exists(copy)) AndAlso copies.Skip(10).All(Function(copy) Not File.Exists(copy)), "only game copies beyond the newest 10 and older than 30 days are removed")

        ' Never while recovery is pending.
        ctx = Context(root, "pending")
        Dim pendingOld = Enumerable.Range(0, 12).Select(Function(i) Session(ctx, TimeSpan.FromDays(40) + TimeSpan.FromHours(i), now)).ToArray()
        Directory.CreateDirectory(New OwnedFileTransaction(ctx).Folder)
        File.WriteAllText(New OwnedFileTransaction(ctx).JournalPath, "{}")
        result = LogCleanup.Run(ctx, now)
        check(result.Skipped = "Recovery pending" AndAlso pendingOld.All(Function(item) Directory.Exists(item)) AndAlso Not File.Exists(LogCleanup.ReceiptPath(ctx)), "nothing is removed while recovery is pending")
        check(LogCleanup.Run(Context(root, "empty"), now).Skipped = "", "an installation without logs is fine")
    End Sub
End Module
