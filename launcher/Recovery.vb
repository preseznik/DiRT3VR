Public Class RecoveryEntry
    Public Property Target As String = ""
    Public Property Backup As String = ""
    Public Property OriginalHash As String = ""
    Public Property AppliedHash As String = ""
    Public Property OriginalExists As Boolean
    Public Property AppliedBackup As String = ""
    ' Older journals lack this field; they were only written for files that were not read-only.
    Public Property OriginalReadOnly As Boolean
End Class

Public Class RecoveryJournal
    Public Property Schema As Integer = 1
    Public Property InstallId As String = ""
    Public Property GraphicsPath As String = ""
    Public Property Entries As New List(Of RecoveryEntry)
End Class

' Fixed target IDs only; no arbitrary destinations from a journal or elevated worker.
Public Class OwnedFileTransaction
    Private ReadOnly context As InstallContext
    Public ReadOnly Folder As String
    Public ReadOnly JournalPath As String

    Public Sub New(value As InstallContext)
        context = value
        Folder = Path.Combine(context.UserRoot, "recovery")
        JournalPath = Path.Combine(Folder, "pending.json")
    End Sub

    Public ReadOnly Property Pending As Boolean
        Get
            Return File.Exists(JournalPath)
        End Get
    End Property

    Private Function TargetPath(target As String) As String
        Select Case target
            Case "proxy" : Return Path.Combine(context.GameRoot, "d3d11.dll")
            Case "graphics" : Return context.GraphicsPath
            Case "restrictions" : Return Path.Combine(context.GameRoot, "system", "hardware_settings_restrictions.xml")
            Case "startup" : Return Path.Combine(context.GameRoot, "system", "states.bin")
            Case Else : Throw New IOException("Unknown recovery target. The journal was preserved.")
        End Select
    End Function

    Public Sub Prepare(changes As IReadOnlyDictionary(Of String, Byte()), Optional experimental As Boolean = False, Optional apply As Boolean = True)
        context.RequireClosed()
        If Pending Then Throw New IOException("File recovery is pending. Restore original files first.")
        If changes.Count = 0 Then Return
        Dim hasStartup = changes.ContainsKey("startup")
        Dim journal As New RecoveryJournal With {.Schema = If(hasStartup, 3, If(experimental, 2, 1)), .InstallId = context.Id, .GraphicsPath = context.GraphicsPath}
        Dim prepared = journal.Schema >= 2
        If experimental Then
            Dim required = If(hasStartup, {"graphics", "proxy", "restrictions", "startup"}, {"graphics", "proxy", "restrictions"})
            If Not changes.Keys.Order().SequenceEqual(required) Then Throw New IOException("The experimental session requires its complete set of fixed targets.")
        ElseIf hasStartup AndAlso Not changes.Keys.SequenceEqual({"startup"}) Then
            Throw New IOException("A desktop startup session may change only the startup movie definitions.")
        End If
        ' Check every target before creating backups or the pending journal.
        For Each pair In changes
            Dim target = TargetPath(pair.Key)
            SafeFiles.NoLinks(target)
            If pair.Key = "restrictions" AndAlso Not experimental Then Throw New IOException("Restrictions require an experimental session journal.")
            If pair.Key = "proxy" AndAlso File.Exists(target) Then Throw New IOException("An existing d3d11.dll was preserved. Another graphics mod must be removed using its own instructions.")
            If prepared AndAlso pair.Key <> "proxy" AndAlso Not File.Exists(target) Then Throw New IOException("The original settings file is missing: " & target)
            If prepared Then ValidateApplied(pair.Key, If(File.Exists(target), File.ReadAllBytes(target), Array.Empty(Of Byte)()), pair.Value)
        Next
        For Each pair In changes
            Dim target = TargetPath(pair.Key)
            SafeFiles.NoLinks(target)
            If pair.Key = "proxy" AndAlso File.Exists(target) Then Throw New IOException("An existing d3d11.dll was preserved. Another graphics mod must be removed using its own instructions.")
            Dim entry As New RecoveryEntry With {.Target = pair.Key, .OriginalExists = File.Exists(target), .AppliedHash = SafeFiles.HashBytes(pair.Value), .Backup = Guid.NewGuid().ToString("N") & ".bin"}
            If entry.OriginalExists Then
                ' Read-only files stay read-only while applied (WriteAtomic keeps attributes); recovery puts the recorded flag back.
                entry.OriginalReadOnly = SafeFiles.IsReadOnly(target)
                Dim bytes = File.ReadAllBytes(target)
                entry.OriginalHash = SafeFiles.HashBytes(bytes)
                SafeFiles.WriteAtomic(Path.Combine(Folder, entry.Backup), bytes)
            End If
            If prepared Then
                entry.AppliedBackup = Guid.NewGuid().ToString("N") & ".bin"
                SafeFiles.WriteAtomic(Path.Combine(Folder, entry.AppliedBackup), pair.Value)
            End If
            journal.Entries.Add(entry)
        Next
        ' All backups and the ownership journal exist before the first target write.
        SafeFiles.SaveJson(JournalPath, journal)
        If prepared Then
            If apply Then ApplyPrepared()
            Return
        End If
        For Each entry In journal.Entries
            Dim target = TargetPath(entry.Target)
            If File.Exists(target) <> entry.OriginalExists OrElse (entry.OriginalExists AndAlso SafeFiles.Hash(target) <> entry.OriginalHash) Then Throw New IOException("The game file changed during preparation: " & target)
            WriteTarget(target, changes(entry.Target), entry.OriginalExists)
        Next
    End Sub

    Private Shared Sub WriteTarget(path As String, bytes As Byte(), exists As Boolean)
        If exists Then
            SafeFiles.WriteAtomic(path, bytes)
        Else
            SafeFiles.NoLinks(path)
            Using stream As New FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                stream.Write(bytes) : stream.Flush(True)
            End Using
        End If
        If SafeFiles.Hash(path) <> SafeFiles.HashBytes(bytes) Then Throw New IOException("Deployed file verification failed: " & path)
    End Sub

    Private Sub ValidateApplied(target As String, original As Byte(), applied As Byte())
        Dim expected As Byte()
        Select Case target
            Case "graphics"
                Dim size = ExperimentalFiles.AppliedGraphicsSize(applied)
                expected = ExperimentalFiles.Graphics(original, size.Width, size.Height)
            Case "restrictions" : expected = ExperimentalFiles.Restrictions(original)
            Case "startup" : expected = StartupMovies.Patch(original)
            Case "proxy"
                Dim payload = Path.Combine(context.PackageRoot, "payload", "d3d11.dll")
                SafeFiles.NoLinks(payload) : expected = File.ReadAllBytes(payload)
            Case Else : Throw New IOException("Unknown prepared session target.")
        End Select
        If Not expected.SequenceEqual(applied) Then Throw New IOException("Prepared session data does not match its approved transformation: " & target)
    End Sub

    Private Function LoadJournal() As RecoveryJournal
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(JournalPath)
        If Not {1, 2, 3}.Contains(journal.Schema) OrElse journal.InstallId <> context.Id OrElse journal.Entries Is Nothing OrElse journal.Entries.Count < 1 OrElse journal.Entries.Count > If(journal.Schema = 3, 4, If(journal.Schema = 2, 3, 2)) OrElse journal.Entries.Any(Function(entry) entry Is Nothing) OrElse journal.Entries.Select(Function(e) e.Target).Distinct().Count() <> journal.Entries.Count Then Throw New IOException("Invalid recovery journal. Backups were preserved.")
        If Not String.Equals(journal.GraphicsPath, context.GraphicsPath, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("Graphics recovery belongs to the Windows account that started this session. The original backup was preserved.")
        Dim targets = journal.Entries.Select(Function(entry) entry.Target).Order().ToArray()
        Select Case journal.Schema
            Case 1
                If targets.Any(Function(target) target <> "graphics" AndAlso target <> "proxy") Then Throw New IOException("Invalid legacy recovery target.")
            Case 2
                If Not targets.SequenceEqual({"graphics", "proxy", "restrictions"}) Then Throw New IOException("Incomplete experimental recovery journal.")
            Case 3
                If Not targets.SequenceEqual({"startup"}) AndAlso Not targets.SequenceEqual({"graphics", "proxy", "restrictions", "startup"}) Then Throw New IOException("Incomplete startup recovery journal.")
        End Select
        Return journal
    End Function

    Private Shared Function ValidBackup(name As String) As Boolean
        Dim id As Guid
        Return name IsNot Nothing AndAlso name.Length = 36 AndAlso name.EndsWith(".bin", StringComparison.Ordinal) AndAlso Guid.TryParseExact(name.Substring(0, 32), "N", id)
    End Function

    Private Function CheckedApplied(journal As RecoveryJournal, entry As RecoveryEntry, Optional deployment As Boolean = False) As Byte()
        If journal.Schema < 2 Then Return Nothing
        If Not ValidBackup(entry.AppliedBackup) OrElse entry.AppliedBackup = entry.Backup Then Throw New IOException("Invalid applied backup name.")
        Dim path = IO.Path.Combine(Folder, entry.AppliedBackup)
        SafeFiles.NoLinks(path)
        If Not File.Exists(path) OrElse SafeFiles.Hash(path) <> entry.AppliedHash Then Throw New IOException("Applied backup is missing or changed.")
        Dim applied = File.ReadAllBytes(path)
        If SafeFiles.HashBytes(applied) <> entry.AppliedHash Then Throw New IOException("Applied backup changed during inspection.")
        ' Recovery remains possible after the package was moved or updated.
        If deployment OrElse entry.Target <> "proxy" Then ValidateApplied(entry.Target, If(entry.OriginalExists, File.ReadAllBytes(IO.Path.Combine(Folder, entry.Backup)), Array.Empty(Of Byte)()), applied)
        Return applied
    End Function

    Public Sub ApplyPrepared()
        context.RequireClosed() : context.ValidateGame()
        Dim journal = LoadJournal()
        If journal.Schema < 2 Then Throw New IOException("Only a prepared session may be deployed.")
        Preflight(journal, False)
        For Each entry In journal.Entries
            CheckedApplied(journal, entry, deployment:=True)
        Next
        For Each entry In journal.Entries
            Dim target = TargetPath(entry.Target), current = If(File.Exists(target), SafeFiles.Hash(target), "")
            If current = entry.AppliedHash Then Continue For
            If current <> If(entry.OriginalExists, entry.OriginalHash, "") Then Throw New IOException("File changed during deployment; recovery is pending.")
            WriteTarget(target, CheckedApplied(journal, entry, deployment:=True), entry.OriginalExists)
        Next
    End Sub

    Public Function Recover() As Boolean
        context.RequireClosed()
        If Not Pending Then Return False
        Dim journal = LoadJournal()
        Dim observed = Preflight(journal, True)
        For Each entry In journal.Entries
            Dim target = TargetPath(entry.Target)
            SafeFiles.NoLinks(target)
            Dim current = If(File.Exists(target), SafeFiles.Hash(target), "")
            Dim original = If(entry.OriginalExists, entry.OriginalHash, "")
            If current <> observed(entry.Target) Then Throw New IOException("Recovery conflict: the file changed during restoration. Backups were preserved.")
            If current <> original Then
                If entry.OriginalExists Then
                    If current <> entry.AppliedHash Then SafeFiles.WriteAtomic(Path.Combine(Folder, "game-written-" & Guid.NewGuid().ToString("N") & ".xml"), File.ReadAllBytes(target))
                    Dim originalBytes = File.ReadAllBytes(Path.Combine(Folder, entry.Backup))
                    If SafeFiles.HashBytes(originalBytes) <> entry.OriginalHash Then Throw New IOException("Original backup changed before restoration.")
                    If SafeFiles.Hash(target) <> current Then Throw New IOException("Graphics file changed before restoration; backups preserved.")
                    SafeFiles.WriteAtomic(target, originalBytes)
                Else
                    SafeFiles.SetReadOnly(target, False)
                    File.Delete(target)
                End If
            End If
            If If(File.Exists(target), SafeFiles.Hash(target), "") <> original Then Throw New IOException("Restored file verification failed; recovery evidence retained.")
            If entry.OriginalExists Then SafeFiles.SetReadOnly(target, entry.OriginalReadOnly)
        Next
        File.Move(JournalPath, Path.Combine(Folder, "restored-" & Guid.NewGuid().ToString("N") & ".json"))
        Return True
    End Function

    Private Function Preflight(journal As RecoveryJournal, allowFormatting As Boolean) As Dictionary(Of String, String)
        Dim observed As New Dictionary(Of String, String)
        ' Preflight the complete journal before restoring any target.
        For Each entry In journal.Entries
            If entry Is Nothing Then Throw New IOException("Invalid recovery entry.")
            Dim target = TargetPath(entry.Target)
            SafeFiles.NoLinks(target)
            If Not ValidBackup(entry.Backup) Then Throw New IOException("Invalid recovery backup name.")
            If entry.AppliedHash Is Nothing OrElse entry.OriginalHash Is Nothing OrElse Not System.Text.RegularExpressions.Regex.IsMatch(entry.AppliedHash, "\A[a-f0-9]{64}\z") OrElse (entry.OriginalExists AndAlso Not System.Text.RegularExpressions.Regex.IsMatch(entry.OriginalHash, "\A[a-f0-9]{64}\z")) Then Throw New IOException("Invalid recovery hashes.")
            If (entry.Target = "restrictions" OrElse entry.Target = "startup") AndAlso journal.Schema = 1 Then Throw New IOException("Invalid legacy recovery target.")
            If journal.Schema >= 2 AndAlso (entry.OriginalExists <> (entry.Target <> "proxy")) Then Throw New IOException("Invalid original-file ownership.")
            If entry.OriginalExists Then
                Dim backup = Path.Combine(Folder, entry.Backup)
                SafeFiles.NoLinks(backup)
                If Not File.Exists(backup) OrElse SafeFiles.Hash(backup) <> entry.OriginalHash Then Throw New IOException("Recovery backup is missing or changed. All files were preserved.")
            End If
            Dim current = If(File.Exists(target), SafeFiles.Hash(target), "")
            Dim original = If(entry.OriginalExists, entry.OriginalHash, "")
            Dim applied = CheckedApplied(journal, entry)
            Dim formattingOnly = allowFormatting AndAlso journal.Schema >= 2 AndAlso entry.Target = "graphics" AndAlso File.Exists(target) AndAlso ExperimentalFiles.SameXml(File.ReadAllBytes(target), applied)
            If current <> original AndAlso current <> entry.AppliedHash AndAlso Not formattingOnly Then Throw New IOException("Recovery conflict: " & target & ". Its contents changed outside DiRT3VR. The current file and original backup were preserved.")
            observed.Add(entry.Target, current)
        Next
        Return observed
    End Function
End Class

Public Module RecoveryWorker
    Public Sub ValidateGraphicsPath(path As String)
        Const suffix As String = "\My Games\DiRT3\hardwaresettings\hardware_settings_config.xml"
        If String.IsNullOrWhiteSpace(path) OrElse Not IO.Path.IsPathFullyQualified(path) OrElse Not IO.Path.GetFullPath(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("The recovery worker only accepts the initiating account's DiRT 3 hardware settings file.")
        SafeFiles.NoLinks(path)
    End Sub

    Public Async Function RecoverAsync(context As InstallContext) As Task(Of Boolean)
        Try
            Return Await Task.Run(Function() New OwnedFileTransaction(context).Recover())
        Catch ex As UnauthorizedAccessException
            ' Retry only the file operation under elevation; preferences stay with the owner.
        End Try
        Await RunWorkerAsync(context, "--recover-worker")
        Return True
    End Function

    Public Async Function ApplyAsync(context As InstallContext) As Task
        Try
            Await Task.Run(Sub()
                               Dim transaction As New OwnedFileTransaction(context)
                               transaction.ApplyPrepared()
                           End Sub)
            Return
        Catch ex As UnauthorizedAccessException
            ' The prepared receipt remains unchanged for the file-only worker.
        End Try
        Await RunWorkerAsync(context, "--apply-worker")
    End Function

    Private Async Function RunWorkerAsync(context As InstallContext, mode As String) As Task
        ValidateGraphicsPath(context.GraphicsPath)
        Dim start As New ProcessStartInfo(Environment.ProcessPath) With {.UseShellExecute = True, .Verb = "runas", .WindowStyle = ProcessWindowStyle.Hidden}
        For Each item As String In {mode, "--game", context.GameRoot, "--owner-base", Path.GetDirectoryName(context.UserRoot), "--graphics", context.GraphicsPath}
            start.ArgumentList.Add(item)
        Next
        Try
            Using child = Process.Start(start)
                If child Is Nothing Then Throw New IOException("The recovery worker could not start.")
                Await child.WaitForExitAsync()
                If child.ExitCode <> 0 Then Throw New IOException("The recovery worker did not finish. Original backups were preserved; see recovery-worker.json in the logs folder.")
            End Using
        Catch ex As ComponentModel.Win32Exception When ex.NativeErrorCode = 1223
            Throw New IOException("Administrator recovery was cancelled. Backups and pending recovery were preserved.", ex)
        End Try
    End Function
End Module
