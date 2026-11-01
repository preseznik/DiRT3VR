Imports System.Drawing
Imports System.Net.Http
Imports System.Threading
Imports System.Windows.Forms

' Help / About with update checks against GitHub Releases (adapted from DiRT2VR's AboutForm).
Public NotInheritable Class AboutForm
    Inherits LauncherForm

    Public Event UpdatePreferenceChanged()
    Private ReadOnly context As InstallContext
    Private ReadOnly client As HttpClient
    Private ReadOnly ownsClient As Boolean
    Private ReadOnly sessionBlocker As Func(Of String)
    Private ReadOnly startInstaller As Action(Of ProcessStartInfo)
    Private ReadOnly confirmRollback As Func(Of ReleaseUpdate, Boolean)
    Private ReadOnly cancellation As New CancellationTokenSource()
    Private ReadOnly checkButton As New Button With {.Name = "CheckUpdates", .Text = "Check for updates", .AutoSize = True}
    Private ReadOnly installButton As New Button With {.Name = "InstallUpdate", .Text = "Download and install", .AutoSize = True, .Enabled = False}
    Private ReadOnly notesButton As New Button With {.Name = "UpdateReleaseNotes", .Text = "Release notes", .AutoSize = True, .Enabled = False}
    Private ReadOnly rollbackButton As New Button With {.Name = "ReturnToStable", .Text = "Return to stable…", .AutoSize = True, .Visible = BuildInfo.Experimental}
    Private ReadOnly experimental As New CheckBox With {.Name = "ExperimentalUpdates", .Text = "Include experimental releases", .AutoSize = True}
    Private ReadOnly status As New Label With {.Name = "UpdateStatus", .AutoSize = True, .UseMnemonic = False, .Text = "Check GitHub Releases for a newer version. No GitHub account is needed."}
    Private ReadOnly downloadBar As New ProgressBar With {.Name = "UpdateProgress", .Visible = False, .Width = 200, .Height = 16}
    Private availableUpdate As ReleaseUpdate
    Private working As Boolean
    Private checkGeneration As Integer
    Private restoringPreference As Boolean

    Public Sub New(context As InstallContext, topics As IReadOnlyList(Of HelpTopic), Optional knownUpdate As ReleaseUpdate = Nothing, Optional updateClient As HttpClient = Nothing,
                   Optional blocker As Func(Of String) = Nothing, Optional installerStarter As Action(Of ProcessStartInfo) = Nothing,
                   Optional rollbackConfirmation As Func(Of ReleaseUpdate, Boolean) = Nothing, Optional initialTopic As String = Nothing)
        Me.context = context
        ownsClient = updateClient Is Nothing
        client = If(updateClient, UpdateService.CreateClient())
        sessionBlocker = If(blocker, New Func(Of String)(Function() ""))
        startInstaller = If(installerStarter, New Action(Of ProcessStartInfo)(Sub(start) Process.Start(start)?.Dispose()))
        confirmRollback = If(rollbackConfirmation, New Func(Of ReleaseUpdate, Boolean)(
            Function(target) MessageBox.Show(Me, "Installed: " & BuildInfo.Version & " (Experimental)" & vbCrLf & "Stable: " & target.Version.Text & vbCrLf & vbCrLf &
                "Install the stable release? Your launcher preferences are backed up first and experimental updates are turned off.",
                "Return to stable", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) = DialogResult.OK))

        Text = "DiRT3VR — About, updates and instructions"
        StartPosition = FormStartPosition.CenterParent
        ' Logical sizes scaled for the display (this window doesn't autoscale), so the topic list fits at 150% too.
        Dim work = Screen.PrimaryScreen.WorkingArea
        MinimumSize = New Size(Px(Me, 520), Px(Me, 440))
        Size = New Size(Math.Max(MinimumSize.Width, Math.Min(Px(Me, 820), work.Width)), Math.Max(MinimumSize.Height, Math.Min(Px(Me, 700), work.Height)))
        Padding = New Padding(Px(Me, 20))
        ' The Updates stack sizes itself to its rows; the instruction topics fill the rest of the window.
        Dim updates As New VerticalStack With {.Name = "Updates", .Dock = DockStyle.Top, .Padding = New Padding(0, 0, 0, 12)}
        updates.Controls.Add(New Label With {.Text = "Updates", .AutoSize = True, .Font = New Font("Segoe UI", 11, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 6)})
        updates.Controls.Add(New Label With {.Name = "InstalledVersion", .AutoSize = True, .Margin = New Padding(0), .Text = "Installed: DiRT3VR " & BuildInfo.Version & " (" & BuildInfo.Channel & ")"})
        status.Margin = New Padding(0, 6, 0, 6)
        updates.Controls.Add(status)
        Dim buttons As New FlowLayoutPanel With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .WrapContents = True, .Margin = New Padding(0)}
        Dim releases As New LinkLabel With {.Name = "ReleasesLink", .Text = "Releases on GitHub", .AutoSize = True, .Margin = New Padding(8, 9, 0, 0)}
        downloadBar.Margin = New Padding(8, 10, 0, 0)
        buttons.Controls.AddRange({checkButton, installButton, notesButton, rollbackButton, releases, downloadBar})
        updates.Controls.Add(buttons) : updates.Controls.Add(experimental)
        ' Fill is added before Top so it takes the remaining space; tab order still starts with Updates.
        Instructions = New InstructionsView(topics, initialTopic) With {.Dock = DockStyle.Fill, .TabIndex = 1}
        Controls.Add(Instructions)
        updates.TabIndex = 0
        Controls.Add(updates)

        experimental.Checked = UpdatePreferences.Load(context).IncludeExperimentalReleases
        AddHandler experimental.CheckedChanged, Sub() ExperimentalChanged()
        AddHandler checkButton.Click, Async Sub() Await CheckUpdateAsync(False)
        AddHandler installButton.Click, Async Sub() Await InstallUpdateAsync()
        AddHandler rollbackButton.Click, Async Sub() Await CheckUpdateAsync(True)
        AddHandler notesButton.Click, Sub() If availableUpdate IsNot Nothing Then OpenUrl(availableUpdate.Page)
        AddHandler releases.LinkClicked, Sub() OpenUrl(UpdateService.Releases)
        AddHandler SizeChanged, Sub() status.MaximumSize = New Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 8), 0)
        status.MaximumSize = New Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 8), 0)
        If knownUpdate IsNot Nothing AndAlso (experimental.Checked OrElse Not knownUpdate.IsExperimental) Then
            availableUpdate = knownUpdate : DescribeUpdate()
        End If
        SetWorking(False)
    End Sub

    Public ReadOnly Property InstallerStarted As Boolean

    ' The instruction topics (the launcher opens the one for the section whose link was clicked).
    Public ReadOnly Property Instructions As InstructionsView

    Private Shared Sub OpenUrl(url As String)
        Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})?.Dispose()
    End Sub

    Private Sub SetWorking(value As Boolean)
        working = value
        checkButton.Enabled = Not value : experimental.Enabled = Not value : rollbackButton.Enabled = Not value
        notesButton.Enabled = Not value AndAlso availableUpdate IsNot Nothing
        installButton.Enabled = Not value AndAlso availableUpdate IsNot Nothing AndAlso availableUpdate.Download IsNot Nothing
    End Sub

    Private Sub ExperimentalChanged()
        If restoringPreference Then Return
        Try
            UpdatePreferences.Save(context, experimental.Checked)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            restoringPreference = True : experimental.Checked = Not experimental.Checked : restoringPreference = False
            status.Text = "Could not save the update preference: " & ex.Message
            Return
        End Try
        availableUpdate = Nothing : checkGeneration += 1 : SetWorking(False)
        status.Text = If(experimental.Checked, "Experimental releases are included. Check for updates to look for one.", "Only stable releases are offered.")
        RaiseEvent UpdatePreferenceChanged()
    End Sub

    Private Sub DescribeUpdate()
        If availableUpdate Is Nothing Then
            status.Text = "You have the latest " & If(experimental.Checked, "release", "stable release") & " (" & BuildInfo.Version & ")."
        ElseIf availableUpdate.Download Is Nothing Then
            status.Text = availableUpdate.Describe() & " is available, but without a verified installer. Open Release notes to install it manually."
        Else
            status.Text = availableUpdate.Describe() & " is available (" & Math.Ceiling(availableUpdate.Size / 1048576.0).ToString(Globalization.CultureInfo.InvariantCulture) & " MB)."
        End If
    End Sub

    ' Public so the launcher tests can drive it without clicking.
    Public Async Function CheckUpdateAsync(returnToStable As Boolean) As Task
        If working Then Return
        checkGeneration += 1
        Dim generation = checkGeneration
        availableUpdate = Nothing : SetWorking(True)
        status.Text = If(returnToStable, "Finding the latest stable release…", "Checking GitHub Releases…")
        Try
            Dim service As New UpdateService(client)
            Dim result As ReleaseUpdate
            If returnToStable Then
                result = Await service.CheckStableAsync(cancellation.Token)
            Else
                result = Await service.CheckAsync(BuildInfo.Version, experimental.Checked, cancellation.Token)
            End If
            If IsDisposed OrElse generation <> checkGeneration Then Return
            If Not returnToStable Then
                availableUpdate = result : DescribeUpdate()
            ElseIf result Is Nothing OrElse result.Download Is Nothing Then
                status.Text = "A verified stable installer is unavailable. Nothing was changed."
            ElseIf confirmRollback(result) Then
                Await InstallCoreAsync(result, True)
            Else
                status.Text = "Return to stable canceled. Nothing was changed."
            End If
        Catch ex As OperationCanceledException
            If Not IsDisposed AndAlso generation = checkGeneration Then status.Text = "The update check was canceled or timed out. Try again when you're online."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is System.Text.Json.JsonException
            If Not IsDisposed AndAlso generation = checkGeneration Then status.Text = If(ex.Message = UpdateService.NoPublicReleases, ex.Message, "Could not check for updates: " & ex.Message)
        Finally
            If Not IsDisposed AndAlso generation = checkGeneration Then SetWorking(False)
        End Try
    End Function

    Public Async Function InstallUpdateAsync() As Task
        If working Then Return
        SetWorking(True)
        Try
            Await InstallCoreAsync(availableUpdate, False)
        Finally
            If Not IsDisposed Then SetWorking(False)
        End Try
    End Function

    Private Async Function InstallCoreAsync(target As ReleaseUpdate, rollback As Boolean) As Task
        If target Is Nothing OrElse target.Download Is Nothing Then Return
        If Not rollback AndAlso target.Version.CompareTo(ReleaseVersion.Parse(BuildInfo.Version)) <= 0 Then Return
        If target.IsExperimental AndAlso Not UpdatePreferences.Load(context).IncludeExperimentalReleases Then Return
        Dim blocked = sessionBlocker()
        If blocked <> "" Then
            status.Text = blocked
            Return
        End If
        downloadBar.Value = 0 : downloadBar.Visible = True
        Dim downloading = True
        Try
            context.RequireClosed()
            status.Text = "Downloading " & target.Describe() & "… Close this window to cancel."
            Dim reporter As New System.Progress(Of Integer)(Sub(value)
                                                         If IsDisposed OrElse Not downloading Then Return
                                                         downloadBar.Value = Math.Clamp(value, 0, 100)
                                                         If value = 100 Then status.Text = "Verifying the download… Close this window to cancel."
                                                     End Sub)
            Dim installer = Await New UpdateService(client).DownloadAsync(target, Path.Combine(context.UserRoot, "updates"), reporter, cancellation.Token)
            downloading = False
            status.Text = "Restoring original game files before setup…"
            Await UpdateService.PrepareInstallerAsync(context, cancellation.Token)
            If rollback Then UpdatePreferences.PrepareRollback(context)
            cancellation.Token.ThrowIfCancellationRequested()
            status.Text = "Opening setup… Windows may ask for administrator approval."
            startInstaller(UpdateService.InstallerStartInfo(installer, context.GameRoot))
            _InstallerStarted = True
            ' Setup waits until DiRT3VR has closed (Global\DiRT3VR.Session), so close the launcher too.
            Dim launcher = Owner
            Close()
            launcher?.Close()
        Catch ex As OperationCanceledException
            If Not IsDisposed Then status.Text = "The update was canceled or timed out. Setup was not started."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.ComponentModel.Win32Exception
            If Not IsDisposed Then status.Text = "The update could not start: " & ex.Message
        Finally
            downloading = False
            If Not IsDisposed Then downloadBar.Visible = False
        End Try
    End Function

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If Not resourcesDisposed Then cancellation.Cancel()
        MyBase.OnFormClosing(e)
    End Sub

    ' Closing a window shown without ShowDialog already disposes it; a later Dispose must be harmless.
    Private resourcesDisposed As Boolean
    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso Not resourcesDisposed Then
            resourcesDisposed = True
            cancellation.Cancel() : cancellation.Dispose()
            If ownsClient Then client.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
