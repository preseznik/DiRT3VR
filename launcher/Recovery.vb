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
    ' VR-only graphics quality the session's graphics file was prepared with (approved transform). Only
    ' written when set, so other journals stay readable by older launchers; those refuse one that has it.
    <System.Text.Json.Serialization.JsonIgnore(Condition:=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)>
    Public Property GraphicsQuality As Dictionary(Of String, String)
    ' Schema 6: the seat offset each car's camera file was prepared with (approved transform).
    <System.Text.Json.Serialization.JsonIgnore(Condition:=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)>
    Public Property Seats As Dictionary(Of String, SeatOffset)
End Class

' What to do with a graphics file that changed during the session beyond DiRT3VR's own values.
Public Enum GraphicsRecoveryChoice
    Ask = 0              ' Leave it as it is, keep the journal pending and report it.
    KeepGameChanges = 1  ' Put back the session's values (resolution, VSync, MSAA, DirectX…), keep the rest.
    RestoreOriginal = 2  ' Restore the file from before the session.
End Enum

Public Enum GraphicsConflictKind
    None = 0
    Mergeable = 1     ' Both choices are possible.
    NotMergeable = 2  ' Not DiRT 3's settings XML any more: only the original can be restored.
End Enum

' Recovery restored every file it could; these targets changed outside DiRT3VR and were left as they are.
Public Class RecoveryConflictException
    Inherits IOException
    Public ReadOnly Property Targets As IReadOnlyList(Of String)
    Public Sub New(message As String, conflicts As IReadOnlyList(Of String))
        MyBase.New(message)
        Targets = conflicts
    End Sub
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
        If SeatPositions.IsTarget(target) Then Return SeatPositions.CameraPath(context, SeatPositions.CodeOf(target))
        Select Case target
            Case "proxy" : Return Path.Combine(context.GameRoot, "d3d11.dll")
            Case "direct" : Return DirectPractice.ConfigPath(context)
            Case "graphics" : Return context.GraphicsPath
            Case "restrictions" : Return Path.Combine(context.GameRoot, "system", "hardware_settings_restrictions.xml")
            Case "startup" : Return Path.Combine(context.GameRoot, "system", "states.bin")
            Case MotionBlur.Target : Return MotionBlur.EffectsPath(context)
            Case Else : Throw New IOException("Unknown recovery target. The journal was preserved.")
        End Select
    End Function

    Public Sub Prepare(changes As IReadOnlyDictionary(Of String, Byte()), Optional experimental As Boolean = False, Optional apply As Boolean = True, Optional vrQuality As IDictionary(Of String, String) = Nothing,
                       Optional seats As IReadOnlyDictionary(Of String, SeatOffset) = Nothing)
        context.RequireClosed()
        If Pending Then Throw New IOException("File recovery is pending. Restore original files first.")
        If changes.Count = 0 Then Return
        Dim hasStartup = changes.ContainsKey("startup")
        Dim quality = GraphicsCatalog.Current.Normalize(vrQuality)
        If quality.Count > 0 AndAlso Not (experimental AndAlso changes.ContainsKey("graphics")) Then Throw New IOException("VR graphics quality belongs to a VR session.")
        ' Schema 4: a VR session with a direct practice config (and optionally startup). Schema 5: a desktop
        ' direct practice session (config and renderer, optionally startup; no graphics or restrictions).
        Dim hasDirect = changes.ContainsKey("direct")
        ' Schema 6: a VR session that also moves seats (one camera file per car, with its offset recorded).
        Dim seatCodes = changes.Keys.Where(AddressOf SeatPositions.IsTarget).Select(AddressOf SeatPositions.CodeOf).OrderBy(Function(code) code, StringComparer.Ordinal).ToArray()
        Dim hasSeats = seatCodes.Length > 0
        If hasSeats AndAlso Not experimental Then Throw New IOException("Seat positions belong to a VR session.")
        If Not If(seats?.Keys.OrderBy(Function(code) code, StringComparer.Ordinal).SequenceEqual(seatCodes), Not hasSeats) OrElse (seats IsNot Nothing AndAlso seats.Values.Any(Function(offset) offset Is Nothing OrElse offset.IsUnchanged)) Then Throw New IOException("Every seat change needs its own offset.")
        For Each offset In If(seats?.Values, Array.Empty(Of SeatOffset)())
            offset.Validate()
        Next
        ' Schema 7: a VR session that also switches motion blur off (postprocess\effects.xml), with or without seats.
        Dim hasEffects = changes.ContainsKey(MotionBlur.Target)
        If hasEffects AndAlso Not experimental Then Throw New IOException("Motion blur changes belong to a VR session.")
        Dim journal As New RecoveryJournal With {.Schema = If(hasEffects, 7, If(hasSeats, 6, If(hasDirect, If(experimental, 4, 5), If(hasStartup, 3, If(experimental, 2, 1))))), .InstallId = context.Id, .GraphicsPath = context.GraphicsPath, .GraphicsQuality = If(quality.Count > 0, quality, Nothing),
                                                 .Seats = If(hasSeats, seats.ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value.Copy(), StringComparer.Ordinal), Nothing)}
        Dim prepared = journal.Schema >= 2
        If experimental Then
            Dim required = {"graphics", "proxy", "restrictions"}.Concat(If(hasStartup, {"startup"}, Array.Empty(Of String)())).Concat(If(hasDirect, {"direct"}, Array.Empty(Of String)())).Concat(If(hasEffects, {MotionBlur.Target}, Array.Empty(Of String)())).Order().ToArray()
            If Not changes.Keys.Where(Function(key) Not SeatPositions.IsTarget(key)).Order().SequenceEqual(required) Then Throw New IOException("The experimental session requires its complete set of fixed targets.")
        ElseIf hasDirect Then
            Dim required = {"direct", "proxy"}.Concat(If(hasStartup, {"startup"}, Array.Empty(Of String)())).Order().ToArray()
            If Not changes.Keys.Order().SequenceEqual(required) Then Throw New IOException("A desktop direct practice session changes only its config, DiRT3VR's renderer and optionally the startup movie definitions.")
        ElseIf hasStartup AndAlso Not changes.Keys.SequenceEqual({"startup"}) Then
            Throw New IOException("A desktop startup session may change only the startup movie definitions.")
        End If
        ' Check every target before creating backups or the pending journal.
        For Each pair In changes
            Dim target = TargetPath(pair.Key)
            SafeFiles.NoLinks(target)
            If pair.Key = "restrictions" AndAlso Not experimental Then Throw New IOException("Restrictions require an experimental session journal.")
            If pair.Key = "proxy" AndAlso File.Exists(target) Then Throw New IOException("An existing d3d11.dll was preserved. Another graphics mod must be removed using its own instructions.")
            If pair.Key = "direct" AndAlso File.Exists(target) Then Throw New IOException("A " & DirectPractice.FileName & " already exists in the game folder; it was preserved. Remove it if it isn't needed, then launch again.")
            If prepared AndAlso Not Created(pair.Key) AndAlso Not File.Exists(target) Then Throw New IOException("The original settings file is missing: " & target)
            If prepared Then ValidateApplied(pair.Key, If(File.Exists(target), File.ReadAllBytes(target), Array.Empty(Of Byte)()), pair.Value, journal)
        Next
        For Each pair In changes
            Dim target = TargetPath(pair.Key)
            SafeFiles.NoLinks(target)
            If pair.Key = "proxy" AndAlso File.Exists(target) Then Throw New IOException("An existing d3d11.dll was preserved. Another graphics mod must be removed using its own instructions.")
            If pair.Key = "direct" AndAlso File.Exists(target) Then Throw New IOException("A " & DirectPractice.FileName & " already exists in the game folder; it was preserved.")
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

    Private Sub ValidateApplied(target As String, original As Byte(), applied As Byte(), journal As RecoveryJournal)
        Dim expected As Byte()
        Dim vrQuality = journal.GraphicsQuality
        If SeatPositions.IsTarget(target) Then
            Dim offset As SeatOffset = Nothing
            If journal.Seats Is Nothing OrElse Not journal.Seats.TryGetValue(SeatPositions.CodeOf(target), offset) OrElse offset Is Nothing Then Throw New IOException("Unknown prepared session target.")
            expected = SeatPositions.Apply(original, offset)
            If Not expected.SequenceEqual(applied) Then Throw New IOException("Prepared session data does not match its approved transformation: " & target)
            Return
        End If
        Select Case target
            Case "graphics"
                Dim size = ExperimentalFiles.AppliedGraphicsSize(applied)
                expected = ExperimentalFiles.Graphics(original, size.Width, size.Height, vrQuality)
            Case "restrictions" : expected = ExperimentalFiles.Restrictions(original)
            Case "startup" : expected = StartupMovies.Patch(original)
            Case "direct" : DirectPractice.Validate(applied) : expected = applied
            Case MotionBlur.Target : expected = MotionBlur.Disable(original)
            Case "proxy"
                Dim payload = Path.Combine(context.PackageRoot, "payload", "d3d11.dll")
                SafeFiles.NoLinks(payload) : expected = File.ReadAllBytes(payload)
            Case Else : Throw New IOException("Unknown prepared session target.")
        End Select
        If Not expected.SequenceEqual(applied) Then Throw New IOException("Prepared session data does not match its approved transformation: " & target)
    End Sub

    Private Function LoadJournal() As RecoveryJournal
        Dim journal = SafeFiles.ReadJson(Of RecoveryJournal)(JournalPath)
        If Not {1, 2, 3, 4, 5, 6, 7}.Contains(journal.Schema) OrElse journal.InstallId <> context.Id OrElse journal.Entries Is Nothing OrElse journal.Entries.Count < 1 OrElse journal.Entries.Count > MaxEntries(journal.Schema) OrElse journal.Entries.Any(Function(entry) entry Is Nothing) OrElse journal.Entries.Select(Function(e) e.Target).Distinct().Count() <> journal.Entries.Count Then Throw New IOException("Invalid recovery journal. Backups were preserved.")
        If Not String.Equals(journal.GraphicsPath, context.GraphicsPath, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("Graphics recovery belongs to the Windows account that started this session. The original backup was preserved.")
        Dim targets = journal.Entries.Select(Function(entry) entry.Target).Order().ToArray()
        Select Case journal.Schema
            Case 1
                If targets.Any(Function(target) target <> "graphics" AndAlso target <> "proxy") Then Throw New IOException("Invalid legacy recovery target.")
            Case 2
                If Not targets.SequenceEqual({"graphics", "proxy", "restrictions"}) Then Throw New IOException("Incomplete experimental recovery journal.")
            Case 3
                If Not targets.SequenceEqual({"startup"}) AndAlso Not targets.SequenceEqual({"graphics", "proxy", "restrictions", "startup"}) Then Throw New IOException("Incomplete startup recovery journal.")
            Case 4
                If Not targets.SequenceEqual({"direct", "graphics", "proxy", "restrictions"}) AndAlso Not targets.SequenceEqual({"direct", "graphics", "proxy", "restrictions", "startup"}) Then Throw New IOException("Incomplete direct practice recovery journal.")
            Case 5
                If Not targets.SequenceEqual({"direct", "proxy"}) AndAlso Not targets.SequenceEqual({"direct", "proxy", "startup"}) Then Throw New IOException("Incomplete desktop direct practice recovery journal.")
            Case 6, 7
                ' A VR session (with or without startup and direct practice) plus one camera file per seat offset
                ' (schema 6), or plus the motion blur change and optional seat offsets (schema 7).
                Dim fixedTargets = targets.Where(Function(target) Not SeatPositions.IsTarget(target)).ToArray()
                Dim seatCodes = targets.Where(AddressOf SeatPositions.IsTarget).Select(AddressOf SeatPositions.CodeOf).OrderBy(Function(code) code, StringComparer.Ordinal).ToArray()
                Dim sessions = New String()() {New String() {"graphics", "proxy", "restrictions"}, New String() {"graphics", "proxy", "restrictions", "startup"}, New String() {"direct", "graphics", "proxy", "restrictions"}, New String() {"direct", "graphics", "proxy", "restrictions", "startup"}}
                Dim expectedSets = If(journal.Schema = 7, sessions.Select(Function(session) session.Append(MotionBlur.Target).Order().ToArray()).ToArray(), sessions)
                Dim seatsRequired = journal.Schema = 6 OrElse seatCodes.Length > 0
                If Not expectedSets.Any(Function(session) fixedTargets.SequenceEqual(session)) Then Throw New IOException("Incomplete VR session recovery journal.")
                If seatsRequired Then
                    If seatCodes.Length = 0 OrElse journal.Seats Is Nothing OrElse
                       Not journal.Seats.Keys.OrderBy(Function(code) code, StringComparer.Ordinal).SequenceEqual(seatCodes) OrElse journal.Seats.Values.Any(Function(offset) offset Is Nothing OrElse offset.IsUnchanged) Then Throw New IOException("Incomplete seat recovery journal.")
                    For Each offset In journal.Seats.Values
                        offset.Validate()
                    Next
                ElseIf journal.Seats IsNot Nothing Then
                    Throw New IOException("Invalid recovery journal. Backups were preserved.")
                End If
        End Select
        If Not {6, 7}.Contains(journal.Schema) AndAlso journal.Seats IsNot Nothing Then Throw New IOException("Invalid recovery journal. Backups were preserved.")
        Return journal
    End Function

    Private Shared Function MaxEntries(schema As Integer) As Integer
        Select Case schema
            Case 1 : Return 2
            Case 2 : Return 3
            Case 3 : Return 4
            Case 4 : Return 5
            Case 6 : Return 5 + EventCatalog.Current.Cars.Count
            Case 7 : Return 6 + EventCatalog.Current.Cars.Count
            Case Else : Return 3
        End Select
    End Function

    ' Targets DiRT3VR creates for a session (no original to back up; recovery deletes them).
    Private Shared Function Created(target As String) As Boolean
        Return target = "proxy" OrElse target = "direct"
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
        If deployment OrElse entry.Target <> "proxy" Then ValidateApplied(entry.Target, If(entry.OriginalExists, File.ReadAllBytes(IO.Path.Combine(Folder, entry.Backup)), Array.Empty(Of Byte)()), applied, journal)
        Return applied
    End Function

    Public Sub ApplyPrepared()
        context.RequireClosed() : context.ValidateGame()
        Dim journal = LoadJournal()
        If journal.Schema < 2 Then Throw New IOException("Only a prepared session may be deployed.")
        For Each pair In Inspect(journal, False)
            If pair.Value.State = EntryState.Conflict Then Throw New IOException("Recovery conflict: " & TargetPath(pair.Key) & ". Its contents changed outside DiRT3VR. The current file and original backup were preserved.")
        Next
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

    Private Enum EntryState
        Original   ' Already as before the session.
        Applied    ' DiRT3VR's bytes, or the game's equivalent formatting of them: restore the backup.
        Rewritten  ' Graphics: DiRT 3 changed only values the session owns: restore the backup.
        Conflict   ' Changed outside DiRT3VR: left as it is unless the user decides (graphics only).
    End Enum

    ' Restores every target that still holds DiRT3VR's changes, also when another target changed
    ' outside DiRT3VR: a game rewrite of the graphics file must not leave the proxy and patched game
    ' files behind. A changed target is left as it is, the journal stays pending and
    ' RecoveryConflictException names it, unless it is the graphics file and the user chose what to
    ' keep. Damaged recovery evidence still stops everything before any file is touched.
    Public Function Recover(Optional graphicsChoice As GraphicsRecoveryChoice = GraphicsRecoveryChoice.Ask) As Boolean
        context.RequireClosed()
        If Not Pending Then Return False
        Dim journal = LoadJournal()
        Dim states = Inspect(journal, True)
        Dim conflicts As New List(Of String)
        Dim mergeRefused = False
        For Each entry In journal.Entries
            Dim target = TargetPath(entry.Target)
            SafeFiles.NoLinks(target)
            Dim current = If(File.Exists(target), SafeFiles.Hash(target), "")
            Dim expected = If(entry.OriginalExists, entry.OriginalHash, "")
            Dim observed = states(entry.Target)
            If current <> observed.Hash Then Throw New IOException("Recovery conflict: the file changed during restoration. Backups were preserved.")
            Select Case observed.State
                Case EntryState.Original
                Case EntryState.Applied, EntryState.Rewritten
                    If entry.OriginalExists Then
                        If current <> entry.AppliedHash Then KeepGameCopy(target)
                        ReplaceTarget(target, current, OriginalBytes(entry))
                    Else
                        SafeFiles.SetReadOnly(target, False)
                        File.Delete(target)
                    End If
                Case EntryState.Conflict
                    Dim replacement As Byte() = Nothing
                    If entry.Target = "graphics" AndAlso entry.OriginalExists AndAlso File.Exists(target) Then
                        If graphicsChoice = GraphicsRecoveryChoice.RestoreOriginal Then
                            replacement = OriginalBytes(entry)
                        ElseIf graphicsChoice = GraphicsRecoveryChoice.KeepGameChanges Then
                            replacement = ExperimentalFiles.RestoreSessionValues(File.ReadAllBytes(target), OriginalBytes(entry), journal.GraphicsQuality)
                            mergeRefused = replacement Is Nothing
                        End If
                    End If
                    If replacement Is Nothing Then
                        conflicts.Add(entry.Target)
                        Continue For
                    End If
                    KeepGameCopy(target)
                    ReplaceTarget(target, current, replacement)
                    expected = SafeFiles.HashBytes(replacement)
            End Select
            If If(File.Exists(target), SafeFiles.Hash(target), "") <> expected Then Throw New IOException("Restored file verification failed; recovery evidence retained.")
            If entry.OriginalExists Then SafeFiles.SetReadOnly(target, entry.OriginalReadOnly)
        Next
        If conflicts.Count > 0 Then Throw New RecoveryConflictException(ConflictMessage(conflicts, journal.Entries.Count > conflicts.Count, mergeRefused), conflicts)
        File.Move(JournalPath, Path.Combine(Folder, "restored-" & Guid.NewGuid().ToString("N") & ".json"))
        Return True
    End Function

    ' Whether the graphics file is the only target left that changed outside DiRT3VR, so the user can
    ' choose what to keep (read-only; nothing is changed).
    Public Function GraphicsConflict() As GraphicsConflictKind
        If Not Pending Then Return GraphicsConflictKind.None
        Try
            Dim journal = LoadJournal()
            Dim conflicts = Inspect(journal, True).Where(Function(pair) pair.Value.State = EntryState.Conflict).Select(Function(pair) pair.Key).ToArray()
            If Not conflicts.SequenceEqual({"graphics"}) OrElse Not File.Exists(context.GraphicsPath) Then Return GraphicsConflictKind.None
            Dim entry = journal.Entries.Single(Function(item) item.Target = "graphics")
            Return If(ExperimentalFiles.RestoreSessionValues(File.ReadAllBytes(context.GraphicsPath), OriginalBytes(entry), journal.GraphicsQuality) Is Nothing, GraphicsConflictKind.NotMergeable, GraphicsConflictKind.Mergeable)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return GraphicsConflictKind.None
        End Try
    End Function

    Private Function ConflictMessage(conflicts As IReadOnlyList(Of String), othersRestored As Boolean, mergeRefused As Boolean) As String
        Dim parts As New List(Of String)
        If othersRestored Then parts.Add("The other game files were restored.")
        For Each target In conflicts
            If target = "graphics" Then
                parts.Add(If(mergeRefused,
                    "DiRT 3's graphics settings file is no longer valid settings XML, so its changes can't be kept. Use Restore original files and choose to restore the settings from before the session.",
                    "DiRT 3's graphics settings changed during the session beyond DiRT3VR's VR values. Use Restore original files to choose whether to keep those changes or restore the settings from before the session."))
            Else
                parts.Add("Recovery conflict: " & TargetPath(target) & ". Its contents changed outside DiRT3VR (for example a game update, a repair or another mod), so it was left as it is; the original backup was preserved.")
            End If
        Next
        Return String.Join(" ", parts)
    End Function

    Private Function OriginalBytes(entry As RecoveryEntry) As Byte()
        Dim bytes = File.ReadAllBytes(Path.Combine(Folder, entry.Backup))
        If SafeFiles.HashBytes(bytes) <> entry.OriginalHash Then Throw New IOException("Original backup changed before restoration.")
        Return bytes
    End Function

    ' The game's own version of a file DiRT3VR replaces stays in the recovery folder for inspection.
    Private Sub KeepGameCopy(target As String)
        SafeFiles.WriteAtomic(Path.Combine(Folder, "game-written-" & Guid.NewGuid().ToString("N") & ".xml"), File.ReadAllBytes(target))
    End Sub

    Private Shared Sub ReplaceTarget(target As String, observed As String, bytes As Byte())
        If SafeFiles.Hash(target) <> observed Then Throw New IOException("Game file changed before restoration; backups preserved.")
        SafeFiles.WriteAtomic(target, bytes)
    End Sub

    Private Function Inspect(journal As RecoveryJournal, allowGameRewrites As Boolean) As Dictionary(Of String, (Hash As String, State As EntryState))
        Dim states As New Dictionary(Of String, (Hash As String, State As EntryState))
        ' Check the evidence for the complete journal before restoring any target.
        For Each entry In journal.Entries
            If entry Is Nothing Then Throw New IOException("Invalid recovery entry.")
            Dim target = TargetPath(entry.Target)
            SafeFiles.NoLinks(target)
            If Not ValidBackup(entry.Backup) Then Throw New IOException("Invalid recovery backup name.")
            If entry.AppliedHash Is Nothing OrElse entry.OriginalHash Is Nothing OrElse Not System.Text.RegularExpressions.Regex.IsMatch(entry.AppliedHash, "\A[a-f0-9]{64}\z") OrElse (entry.OriginalExists AndAlso Not System.Text.RegularExpressions.Regex.IsMatch(entry.OriginalHash, "\A[a-f0-9]{64}\z")) Then Throw New IOException("Invalid recovery hashes.")
            If (entry.Target = "restrictions" OrElse entry.Target = "startup") AndAlso journal.Schema = 1 Then Throw New IOException("Invalid legacy recovery target.")
            If journal.Schema >= 2 AndAlso entry.OriginalExists = Created(entry.Target) Then Throw New IOException("Invalid original-file ownership.")
            If entry.OriginalExists Then
                Dim backup = Path.Combine(Folder, entry.Backup)
                SafeFiles.NoLinks(backup)
                If Not File.Exists(backup) OrElse SafeFiles.Hash(backup) <> entry.OriginalHash Then Throw New IOException("Recovery backup is missing or changed. All files were preserved.")
            End If
            Dim current = If(File.Exists(target), SafeFiles.Hash(target), "")
            Dim original = If(entry.OriginalExists, entry.OriginalHash, "")
            Dim applied = CheckedApplied(journal, entry)
            Dim state As EntryState
            If current = original Then
                state = EntryState.Original
            ElseIf current = entry.AppliedHash Then
                state = EntryState.Applied
            ElseIf allowGameRewrites AndAlso journal.Schema >= 2 AndAlso entry.Target = "graphics" AndAlso File.Exists(target) Then
                Dim bytes = File.ReadAllBytes(target)
                state = If(ExperimentalFiles.SameXml(bytes, applied), EntryState.Applied, If(ExperimentalFiles.OnlySessionValuesChanged(bytes, applied), EntryState.Rewritten, EntryState.Conflict))
            Else
                state = EntryState.Conflict
            End If
            states.Add(entry.Target, (current, state))
        Next
        Return states
    End Function
End Class

Public Module RecoveryWorker
    Public Sub ValidateGraphicsPath(path As String)
        Const suffix As String = "\My Games\DiRT3\hardwaresettings\hardware_settings_config.xml"
        If String.IsNullOrWhiteSpace(path) OrElse Not IO.Path.IsPathFullyQualified(path) OrElse Not IO.Path.GetFullPath(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("The recovery worker only accepts the initiating account's DiRT 3 hardware settings file.")
        SafeFiles.NoLinks(path)
    End Sub

    ' Every outcome is also written to logs\recovery.json, so a refused restore is visible later.
    Public Async Function RecoverAsync(context As InstallContext, Optional graphicsChoice As GraphicsRecoveryChoice = GraphicsRecoveryChoice.Ask) As Task(Of Boolean)
        Try
            Dim restored = Await RecoverOnceAsync(context, graphicsChoice)
            WriteLog(context, If(restored, "restored", "nothing-pending"), If(restored, "Original game files restored.", "No recovery was pending."), graphicsChoice)
            Return restored
        Catch ex As Exception
            WriteLog(context, "pending", ex.Message, graphicsChoice)
            Throw
        End Try
    End Function

    Private Async Function RecoverOnceAsync(context As InstallContext, graphicsChoice As GraphicsRecoveryChoice) As Task(Of Boolean)
        Try
            Return Await Task.Run(Function() New OwnedFileTransaction(context).Recover(graphicsChoice))
        Catch ex As UnauthorizedAccessException
            ' Retry only the file operation under elevation; preferences stay with the owner.
        End Try
        Await RunWorkerAsync(context, "--recover-worker", "--graphics-choice", ChoiceArgument(graphicsChoice))
        Return True
    End Function

    Private Sub WriteLog(context As InstallContext, outcome As String, message As String, graphicsChoice As GraphicsRecoveryChoice)
        Try
            SafeFiles.SaveJson(Path.Combine(context.UserRoot, "logs", "recovery.json"), New With {.updatedUtc = DateTime.UtcNow, .outcome = outcome, .message = message, .graphicsChoice = ChoiceArgument(graphicsChoice)})
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' The result is still reported to the caller.
        End Try
    End Sub

    Public Function ChoiceArgument(choice As GraphicsRecoveryChoice) As String
        Select Case choice
            Case GraphicsRecoveryChoice.KeepGameChanges : Return "keep"
            Case GraphicsRecoveryChoice.RestoreOriginal : Return "original"
            Case Else : Return "ask"
        End Select
    End Function

    Public Function ParseChoice(value As String) As GraphicsRecoveryChoice
        Select Case value
            Case "ask" : Return GraphicsRecoveryChoice.Ask
            Case "keep" : Return GraphicsRecoveryChoice.KeepGameChanges
            Case "original" : Return GraphicsRecoveryChoice.RestoreOriginal
            Case Else : Throw New IOException("Unknown graphics recovery choice.")
        End Select
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

    Private Async Function RunWorkerAsync(context As InstallContext, mode As String, ParamArray extra As String()) As Task
        ValidateGraphicsPath(context.GraphicsPath)
        Dim start As New ProcessStartInfo(Environment.ProcessPath) With {.UseShellExecute = True, .Verb = "runas", .WindowStyle = ProcessWindowStyle.Hidden}
        For Each item As String In {mode, "--game", context.GameRoot, "--owner-base", Path.GetDirectoryName(context.UserRoot), "--graphics", context.GraphicsPath}.Concat(extra)
            start.ArgumentList.Add(item)
        Next
        Dim startedUtc = DateTime.UtcNow
        Try
            Using child = Process.Start(start)
                If child Is Nothing Then Throw New IOException("The recovery worker could not start.")
                Await child.WaitForExitAsync()
                If child.ExitCode <> 0 Then Throw New IOException(If(WorkerFailure(context, startedUtc), "The recovery worker did not finish. Original backups were preserved; see recovery-worker.json in the logs folder."))
            End Using
        Catch ex As ComponentModel.Win32Exception When ex.NativeErrorCode = 1223
            Throw New IOException("Administrator recovery was cancelled. Backups and pending recovery were preserved.", ex)
        End Try
    End Function

    ' The elevated worker's own explanation (for example a recovery conflict), if it wrote one for this run.
    Public Function WorkerFailure(context As InstallContext, startedUtc As DateTime) As String
        Try
            Dim path = IO.Path.Combine(context.UserRoot, "logs", "recovery-worker.json")
            If Not File.Exists(path) Then Return Nothing
            Using document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path))
                Dim root = document.RootElement
                Dim updated, message As System.Text.Json.JsonElement
                If Not root.TryGetProperty("updatedUtc", updated) OrElse Not root.TryGetProperty("message", message) OrElse message.ValueKind <> System.Text.Json.JsonValueKind.String Then Return Nothing
                Dim written As DateTime
                If updated.ValueKind <> System.Text.Json.JsonValueKind.String OrElse Not updated.TryGetDateTime(written) OrElse written.ToUniversalTime() < startedUtc.AddSeconds(-2) Then Return Nothing
                Return message.GetString()
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Text.Json.JsonException
            Return Nothing
        End Try
    End Function
End Module
