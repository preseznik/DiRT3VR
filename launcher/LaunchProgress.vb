Imports System.Drawing
Imports System.Windows.Forms

' A successful Process.Start is not proof that the game window appeared.
Public Class LaunchProgressState
    Public ReadOnly ProcessId As Integer
    Public ReadOnly StartedUtc As DateTime
    Public Property Finished As Boolean
    Public Property WindowReady As Boolean
    Public Property Message As String = "Waiting for the game window…"

    Public Sub New(pid As Integer, started As DateTime)
        ProcessId = pid : StartedUtc = started
    End Sub

    Public Sub Observe(pid As Integer, started As DateTime, exited As Boolean, windowClass As String, elapsed As TimeSpan, Optional exitCode As Integer? = Nothing)
        If Finished Then Return
        If pid <> ProcessId OrElse started <> StartedUtc Then
            Message = "The original game process is no longer available. Check DiRT 3 or open logs for details."
            Finished = True : Return
        End If
        If exited Then
            Message = "DiRT 3 exited before its game window appeared" & If(exitCode.HasValue, $" (exit code 0x{exitCode.GetValueOrDefault():X8})", "") & ". Open logs for details."
            Finished = True
        ElseIf GameWindow.IsRenderWindowClass(windowClass) Then
            WindowReady = True : Finished = True
            Message = "DiRT 3 is ready. Use Show game if its window is behind another application."
        ElseIf elapsed >= TimeSpan.FromSeconds(90) Then
            Finished = True
            Message = "DiRT 3 has not shown its window after 90 seconds. The game was left running; check Steam or open logs."
        ElseIf windowClass = "InitialLoading" Then
            Message = "Loading DiRT 3… Waiting for the game window after the splash screen."
        End If
    End Sub
End Class

Public Class LaunchProgressForm
    Inherits LauncherForm
    Private ReadOnly stage As New Label With {.Name = "StartupMessage", .AutoSize = True, .Dock = DockStyle.Fill, .MaximumSize = New Size(380, 0)}
    Private ReadOnly elapsedLabel As New Label With {.Name = "StartupElapsed", .AutoSize = True}

    Public Sub New()
        Text = "Launching DiRT 3"
        Font = New Font("Segoe UI", 10)
        AutoScaleMode = AutoScaleMode.Dpi
        ClientSize = New Size(420, 180)
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.Manual
        ShowInTaskbar = False : MinimizeBox = False : MaximizeBox = False
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(16), .ColumnCount = 1, .RowCount = 4}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 12))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.Controls.Add(New Label With {.Text = "Starting DiRT 3", .AutoSize = True, .Font = New Font(Font, FontStyle.Bold)}, 0, 0)
        layout.Controls.Add(stage, 0, 1)
        layout.Controls.Add(New ProgressBar With {.Dock = DockStyle.Fill, .Style = ProgressBarStyle.Marquee, .AccessibleName = "Startup in progress"}, 0, 2)
        Dim footer As New TableLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True, .ColumnCount = 2}
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        Dim hide As New Button With {.Name = "HideLaunchProgress", .Text = "Hide", .AutoSize = True}
        AddHandler hide.Click, Sub() Close()
        CancelButton = hide
        footer.Controls.Add(elapsedLabel, 0, 0) : footer.Controls.Add(hide, 1, 0)
        layout.Controls.Add(footer, 0, 3) : Controls.Add(layout)
        RefreshProgress("Waiting for the game window…", TimeSpan.Zero)
    End Sub

    Protected Overrides Sub OnLoad(e As EventArgs)
        MyBase.OnLoad(e) : CenterToParent()
    End Sub

    Protected Overrides ReadOnly Property ShowWithoutActivation As Boolean
        Get
            Return True
        End Get
    End Property

    Public Sub RefreshProgress(message As String, elapsed As TimeSpan)
        stage.Text = message
        elapsedLabel.Text = $"Elapsed: {Math.Floor(elapsed.TotalMinutes):00}:{elapsed.Seconds:00}"
    End Sub
End Class

Public Class LaunchProgressMonitor
    Implements IDisposable
    Private ReadOnly owner As Form
    Private ReadOnly game As Process
    Private ReadOnly popup As New LaunchProgressForm()
    Private ReadOnly timer As New Timer With {.Interval = 250}
    Private ReadOnly elapsed As Stopwatch = Stopwatch.StartNew()
    Private ReadOnly state As LaunchProgressState
    Private ReadOnly context As InstallContext
    Private ReadOnly experimental As Boolean
    Private ReadOnly ownsProcess As Boolean
    Private ReadOnly borderless As BorderlessWindow
    Private disposed As Boolean
    Public Event Completed(message As String)

    Public Sub New(parent As Form, child As Process, install As InstallContext, Optional vr As Boolean = False, Optional borderlessBounds As Rectangle = Nothing, Optional disposeProcess As Boolean = True)
        owner = parent : game = child
        context = install
        experimental = vr : ownsProcess = Not vr AndAlso disposeProcess
        If Not vr AndAlso borderlessBounds <> Rectangle.Empty Then borderless = New BorderlessWindow(child.Id, borderlessBounds)
        Dim started = DateTime.UtcNow
        Try
            started = child.StartTime.ToUniversalTime()
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is System.ComponentModel.Win32Exception
            ' An immediate process exit is reported by the first poll.
        End Try
        state = New LaunchProgressState(child.Id, started)
        AddHandler timer.Tick, AddressOf Poll
    End Sub

    Public Sub Start()
        popup.Show(owner) : timer.Start() : Poll(Nothing, EventArgs.Empty)
    End Sub

    Private Sub Poll(sender As Object, e As EventArgs)
        If disposed Then Return
        Dim exitCode As Integer? = Nothing
        Dim windowClass = ""
        Try
            game.Refresh()
            Dim exited = game.HasExited
            Dim window = If(exited, IntPtr.Zero, GameWindow.Find(game.Id))
            If exited Then
                exitCode = game.ExitCode
            ElseIf window <> IntPtr.Zero Then
                windowClass = GameWindow.WindowClass(window)
            ElseIf GameWindow.WindowClass(game.MainWindowHandle) = "InitialLoading" Then
                windowClass = "InitialLoading"
            End If
            Dim started = If(exited, state.StartedUtc, game.StartTime.ToUniversalTime())
            Dim placementReady = True
            If borderless IsNot Nothing AndAlso Not exited AndAlso started = state.StartedUtc AndAlso game.Id = state.ProcessId AndAlso GameWindow.IsRenderWindowClass(windowClass) Then
                placementReady = borderless.UpdateWindow(window, Environment.TickCount64)
            End If
            state.Observe(game.Id, started, exited, If(placementReady, windowClass, ""), elapsed.Elapsed, exitCode)
            If Not placementReady AndAlso Not state.Finished Then state.Message = "Applying borderless window placement…"
            If state.WindowReady Then GameWindow.Activate(window)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is System.ComponentModel.Win32Exception
            state.Message = "Could not confirm the game window: " & ex.Message
            state.Finished = True
        End Try
        If state.Finished Then
            borderless?.Cancel()
            Dim message = If(experimental AndAlso state.WindowReady, "DiRT 3's window is ready. Waiting for native headset output; Open logs records its progress.", state.Message)
            If Not String.IsNullOrEmpty(borderless?.Warning) Then message &= " " & borderless.Warning
            Session.WriteLaunchStatus(context, If(state.WindowReady, "ready", "failed"), message,
                                      state.ProcessId, state.StartedUtc, exitCode, windowClass, If(experimental, "experimental-interactive", "desktop"))
            Dispose()
            RaiseEvent Completed(message)
        ElseIf Not popup.IsDisposed Then
            popup.RefreshProgress(state.Message, elapsed.Elapsed)
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If disposed Then Return
        disposed = True : timer.Stop() : timer.Dispose()
        borderless?.Cancel()
        popup.Dispose()
        If ownsProcess Then game.Dispose()
        ' Disposing the process handle never terminates the game.
    End Sub
End Class
