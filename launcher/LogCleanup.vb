Imports System.Text.RegularExpressions

' Removes old diagnostic data automatically (adapted from DiRT2VR's CacheCleanup): VR session log
' folders, including their F8 captures, and the records of recoveries that finished long ago. The
' newest 10 of each and anything younger than 30 days are kept. It never runs while recovery is
' pending or DiRT 3 is running, never follows links, leaves anything it doesn't recognize or that is
' fresh or in use, runs at most once a day for at most a few seconds, and never blocks the launcher.
' Downloaded installers are already replaced by each new download (UpdateService).
Public NotInheritable Class LogCleanup
    Public Class Receipt
        Public Property CompletedUtc As DateTime
        Public Property FilesRemoved As Integer
        Public Property BytesRemoved As Long
        Public Property Skipped As String = ""
        Public Property Problems As New List(Of String)
    End Class

    Public Const KeepNewest As Integer = 10
    Public Shared ReadOnly MaximumAge As TimeSpan = TimeSpan.FromDays(30)
    Private Shared ReadOnly Budget As TimeSpan = TimeSpan.FromSeconds(5)
    Private Shared ReadOnly SessionName As New Regex("\Asession-[0-9a-f]{32}\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly RestoredName As New Regex("\Arestored-[0-9a-f]{32}\.json\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly GameCopyName As New Regex("\Agame-written-[0-9a-f]{32}\.xml\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly BackupName As New Regex("\A[0-9a-f]{32}\.bin\z", RegexOptions.CultureInvariant)

    Private Sub New()
    End Sub

    Public Shared Function ReceiptPath(context As InstallContext) As String
        Return Path.Combine(context.UserRoot, "logs", "cleanup.json")
    End Function

    Public Shared Function RunAsync(context As InstallContext) As Task(Of Receipt)
        Return Task.Run(Function() Run(context, DateTime.UtcNow))
    End Function

    Public Shared Function Run(context As InstallContext, now As DateTime) As Receipt
        Dim result As New Receipt
        Try
            If New OwnedFileTransaction(context).Pending Then
                result.Skipped = "Recovery pending"
                Return result
            End If
            If InstallContext.GameRunning() Then
                result.Skipped = "DiRT 3 is running"
                Return result
            End If
            Dim receiptFile = ReceiptPath(context)
            If CheckedRecently(receiptFile, now) Then
                result.Skipped = "Already checked today"
                Return result
            End If
            Dim watch = Stopwatch.StartNew()
            Dim cutoff = now - MaximumAge
            CleanSessions(context, cutoff, result, watch)
            CleanRecovery(context, cutoff, result, watch)
            result.CompletedUtc = now
            SafeFiles.SaveJson(receiptFile, result)
        Catch ex As Exception
            ' Locked or denied paths and odd files must never get in the way of playing.
            result.Skipped = "Cleanup deferred"
            Problem(result, ex)
        End Try
        Return result
    End Function

    Private Shared Function CheckedRecently(receiptFile As String, now As DateTime) As Boolean
        If Not File.Exists(receiptFile) Then Return False
        Try
            Dim previous = SafeFiles.ReadJson(Of Receipt)(receiptFile)
            Return previous.CompletedUtc <= now AndAlso previous.CompletedUtc > now.AddDays(-1)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is System.Text.Json.JsonException
            Return False ' A damaged receipt is simply replaced.
        End Try
    End Function

    ' logs\session-<id> folders hold one VR session each: native log, session.json, F8 captures.
    Private Shared Sub CleanSessions(context As InstallContext, cutoff As DateTime, result As Receipt, watch As Stopwatch)
        Dim root = Path.Combine(context.UserRoot, "logs")
        If Not Directory.Exists(root) Then Return
        SafeFiles.NoLinks(root)
        Dim entries = Directory.EnumerateDirectories(root).Take(20001).ToArray()
        If entries.Length > 20000 Then Return ' Keep the newest across the complete inventory only.
        Dim sessions = entries.Where(Function(folder) SessionName.IsMatch(Path.GetFileName(folder))).
            OrderByDescending(Function(folder) Directory.GetLastWriteTimeUtc(folder)).ToArray()
        For Each folder In sessions.Skip(KeepNewest)
            If watch.Elapsed > Budget Then Exit For
            Try
                RemoveOldSession(folder, cutoff, result, watch)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Problem(result, ex)
            End Try
        Next
    End Sub

    Private Shared Sub RemoveOldSession(folder As String, cutoff As DateTime, result As Receipt, watch As Stopwatch)
        Dim files As New List(Of String), folders As New List(Of String), pendingFolders As New Stack(Of String)
        pendingFolders.Push(folder)
        ' Check the whole folder before deleting anything: links, fresh or locked files keep it.
        While pendingFolders.Count > 0
            Dim current = pendingFolders.Pop()
            SafeFiles.NoLinks(current)
            If Directory.GetLastWriteTimeUtc(current) >= cutoff Then Return
            folders.Add(current)
            For Each entry In Directory.EnumerateFileSystemEntries(current)
                If files.Count + folders.Count > 25000 OrElse watch.Elapsed > Budget Then Return
                SafeFiles.NoLinks(entry)
                If Directory.Exists(entry) Then
                    pendingFolders.Push(entry)
                Else
                    If File.GetLastWriteTimeUtc(entry) >= cutoff Then Return
                    Using probe As New FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None)
                    End Using
                    files.Add(entry)
                End If
            Next
        End While
        ' A session-shaped name alone doesn't prove the folder is DiRT3VR's.
        Dim names = files.Where(Function(file) Path.GetDirectoryName(file) = folder).Select(Function(file) Path.GetFileName(file)).ToArray()
        If Not names.Any(Function(name) name = "session.json" OrElse (name.StartsWith("native-", StringComparison.Ordinal) AndAlso name.EndsWith(".log", StringComparison.Ordinal)) OrElse name.StartsWith("hud-probe-", StringComparison.Ordinal)) Then Return
        For Each file In files
            Dim size = New FileInfo(file).Length
            SafeFiles.SetReadOnly(file, False)
            IO.File.Delete(file)
            result.FilesRemoved += 1 : result.BytesRemoved += size
        Next
        For Each emptied In folders.OrderByDescending(Function(item) item.Length)
            Directory.Delete(emptied)
        Next
    End Sub

    ' recovery\restored-<id>.json records a finished recovery; the .bin backups it names are only
    ' needed while a journal still refers to them. game-written-<id>.xml keeps the game's version of a
    ' replaced file for inspection.
    Private Shared Sub CleanRecovery(context As InstallContext, cutoff As DateTime, result As Receipt, watch As Stopwatch)
        Dim folder = New OwnedFileTransaction(context).Folder
        If Not Directory.Exists(folder) Then Return
        SafeFiles.NoLinks(folder)
        Dim entries = Directory.EnumerateFiles(folder).Take(20001).Select(Function(file) New FileInfo(file)).ToArray()
        If entries.Length > 20000 Then Return
        Dim journals = entries.Where(Function(file) RestoredName.IsMatch(file.Name)).OrderByDescending(Function(file) file.LastWriteTimeUtc).ToArray()
        Dim expired = journals.Skip(KeepNewest).Where(Function(file) file.LastWriteTimeUtc < cutoff).ToArray()
        ' Backups named by any journal that stays (or that can't be read) are kept.
        Dim keep As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim removable As New List(Of (Journal As FileInfo, Backups As String()))
        For Each journal In journals
            Dim backups As String()
            Try
                backups = NamedBackups(SafeFiles.ReadJson(Of RecoveryJournal)(journal.FullName))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is System.Text.Json.JsonException
                Problem(result, ex)
                Continue For ' Unreadable: neither it nor anything it might name is removed.
            End Try
            If expired.Contains(journal) Then removable.Add((journal, backups)) Else keep.UnionWith(backups)
        Next
        If entries.Any(Function(file) file.Name.Contains("pending", StringComparison.OrdinalIgnoreCase)) Then Return ' A journal appeared meanwhile: try again next time.
        For Each item In removable
            If watch.Elapsed > Budget Then Exit For
            Try
                For Each name In item.Backups.Where(Function(backup) Not keep.Contains(backup))
                    RemoveOldFile(Path.Combine(folder, name), cutoff, result)
                Next
                RemoveOldFile(item.Journal.FullName, cutoff, result)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Problem(result, ex)
            End Try
        Next
        For Each copy In entries.Where(Function(file) GameCopyName.IsMatch(file.Name)).OrderByDescending(Function(file) file.LastWriteTimeUtc).Skip(KeepNewest)
            If watch.Elapsed > Budget Then Exit For
            Try
                RemoveOldFile(copy.FullName, cutoff, result)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Problem(result, ex)
            End Try
        Next
    End Sub

    Private Shared Function NamedBackups(journal As RecoveryJournal) As String()
        If journal?.Entries Is Nothing Then Throw New IOException("Unreadable recovery record.")
        Return journal.Entries.Where(Function(entry) entry IsNot Nothing).
            SelectMany(Function(entry) {entry.Backup, entry.AppliedBackup}).
            Where(Function(name) name IsNot Nothing AndAlso BackupName.IsMatch(name)).ToArray()
    End Function

    Private Shared Sub RemoveOldFile(file As String, cutoff As DateTime, result As Receipt)
        If Not IO.File.Exists(file) Then Return
        SafeFiles.NoLinks(file)
        If IO.File.GetLastWriteTimeUtc(file) >= cutoff Then Return
        Dim size = New FileInfo(file).Length
        SafeFiles.SetReadOnly(file, False)
        IO.File.Delete(file)
        result.FilesRemoved += 1 : result.BytesRemoved += size
    End Sub

    Private Shared Sub Problem(result As Receipt, ex As Exception)
        If result.Problems.Count < 20 Then result.Problems.Add(ex.Message)
    End Sub
End Class
