Imports System.Threading
Imports System.Windows.Forms

' While DiRT 3 runs with temporary file changes, closing the launcher window hides it instead of
' quitting. This notification-area icon keeps the session reachable until DiRT 3 closes and the
' original files are restored; the launcher then quits by itself.
Public NotInheritable Class BackgroundHost
    Implements IDisposable

    Private ReadOnly owner As Form
    Private ReadOnly icon As NotifyIcon
    Private ReadOnly menu As New ContextMenuStrip()
    Private ReadOnly openItem, stopVrItem, endItem As ToolStripMenuItem
    Private ReadOnly vrState As Func(Of Boolean?)
    Private explained As Boolean
    Public ReadOnly Property Active As Boolean

    ' vrState: Nothing without a VR session, True while VR is on, False after Stop VR.
    Public Sub New(owner As Form, openWindow As Action, stopVr As Action, endSession As Action, vrState As Func(Of Boolean?))
        Me.owner = owner : Me.vrState = vrState
        openItem = New ToolStripMenuItem("Open DiRT3VR", Nothing, Sub() openWindow()) With {.Name = "BackgroundOpen"}
        openItem.Font = New Drawing.Font(openItem.Font, Drawing.FontStyle.Bold)
        stopVrItem = New ToolStripMenuItem("Stop VR (keep playing on the monitor)", Nothing, Sub() stopVr()) With {.Name = "BackgroundStopVr"}
        endItem = New ToolStripMenuItem("End session (close DiRT 3)", Nothing, Sub() endSession()) With {.Name = "BackgroundEndSession"}
        menu.Items.AddRange({openItem, New ToolStripSeparator(), stopVrItem, endItem})
        AddHandler menu.Opening, Sub() RefreshMenu()
        icon = New NotifyIcon With {.Icon = owner.Icon, .Text = "DiRT3VR", .ContextMenuStrip = menu, .Visible = False}
        AddHandler icon.DoubleClick, Sub() openWindow()
        AddHandler icon.BalloonTipClicked, Sub() openWindow()
    End Sub

    ' For the launcher tests: the menu as shown after RefreshMenu.
    Public ReadOnly Property MenuItems As ToolStripItemCollection
        Get
            Return menu.Items
        End Get
    End Property

    Public ReadOnly Property IconVisible As Boolean
        Get
            Return icon.Visible
        End Get
    End Property

    Public ReadOnly Property IconText As String
        Get
            Return icon.Text
        End Get
    End Property

    ' Closing the window keeps DiRT3VR running while a session or file operation is in progress,
    ' except when Windows signs out or shuts down: recovery stays journaled for the next start.
    Public Shared Function KeepsRunningOnClose(sessionActive As Boolean, reason As CloseReason) As Boolean
        Return sessionActive AndAlso reason <> CloseReason.WindowsShutDown AndAlso reason <> CloseReason.TaskManagerClosing
    End Function

    ' Once the session ends in the background, DiRT3VR quits by itself unless something needs attention.
    Public Shared Function QuitsAfterSession(recoveryPending As Boolean, failed As Boolean) As Boolean
        Return Not recoveryPending AndAlso Not failed
    End Function

    Public Sub RefreshMenu()
        Dim vr = vrState()
        stopVrItem.Available = vr.HasValue
        stopVrItem.Enabled = vr.GetValueOrDefault()
        stopVrItem.Text = If(vr.HasValue AndAlso Not vr.Value, "VR stopped (DiRT 3 runs on the monitor)", "Stop VR (keep playing on the monitor)")
    End Sub

    ' Hides the launcher window and shows the icon, with a one-time explanation.
    Public Sub Enter(status As String, explanation As String)
        _Active = True
        UpdateStatus(status)
        RefreshMenu()
        icon.Visible = True
        owner.Hide()
        If explained Then Return
        explained = True
        icon.ShowBalloonTip(8000, "DiRT3VR keeps running", explanation & " Open it from here or from the Start menu.", ToolTipIcon.Info)
    End Sub

    ' The tooltip shows the session status (Windows limits it to 127 characters).
    Public Sub UpdateStatus(status As String)
        Dim text = "DiRT3VR · " & status
        icon.Text = If(text.Length > 127, text.Substring(0, 126) & "…", text)
    End Sub

    ' Brings the launcher window back.
    Public Sub Leave()
        _Active = False
        icon.Visible = False
        owner.Show()
        If owner.WindowState = FormWindowState.Minimized Then owner.WindowState = FormWindowState.Normal
        owner.Activate()
    End Sub

    ' The session ended while the window was hidden: say so before the launcher quits.
    Public Sub Finish(message As String)
        icon.ShowBalloonTip(5000, "DiRT3VR", message, ToolTipIcon.Info)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        icon.Visible = False
        icon.Dispose()
        menu.Dispose()
    End Sub
End Class

' One launcher per Windows sign-in. Starting DiRT3VR again (for example from the Start menu while
' it runs in the notification area) shows the running window instead of opening a second one.
Public NotInheritable Class SingleInstance
    Implements IDisposable

    Private ReadOnly instanceMutex As Mutex
    Private ReadOnly showSignal As EventWaitHandle
    Private ReadOnly stopSignal As New ManualResetEvent(False)
    Private ReadOnly owned As Boolean
    Private listener As Thread
    Public ReadOnly Property Primary As Boolean

    Public Sub New(name As String)
        Dim createdNew As Boolean
        Try
            instanceMutex = New Mutex(True, "Local\" & name & ".Launcher", createdNew)
            owned = createdNew
            showSignal = New EventWaitHandle(False, EventResetMode.AutoReset, "Local\" & name & ".Show")
            _Primary = owned
        Catch ex As Exception When TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is WaitHandleCannotBeOpenedException
            ' A launcher started as administrator owns the names; run independently, as before.
            If createdNew Then instanceMutex.ReleaseMutex()
            instanceMutex?.Dispose() : instanceMutex = Nothing : owned = False
            showSignal = Nothing
            _Primary = True
        End Try
    End Sub

    <Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function AllowSetForegroundWindow(processId As UInteger) As Boolean
    End Function

    ' From a second launcher: ask the running one to show its window. The new process was just
    ' started by the user, so it may hand its right to take the foreground to the running one.
    Public Sub SignalPrimary()
        If showSignal Is Nothing OrElse Primary Then Return
        AllowSetForegroundWindow(UInteger.MaxValue) ' ASFW_ANY
        showSignal.Set()
    End Sub

    ' In the running launcher: call onSignal (on a background thread) whenever another one starts.
    Public Sub Listen(onSignal As Action)
        If Not Primary OrElse showSignal Is Nothing OrElse listener IsNot Nothing Then Return
        listener = New Thread(Sub()
                                  Dim waits As WaitHandle() = {showSignal, stopSignal}
                                  While WaitHandle.WaitAny(waits) = 0
                                      onSignal()
                                  End While
                              End Sub) With {.IsBackground = True, .Name = "DiRT3VR single instance"}
        listener.Start()
    End Sub

    ' Must run on the thread that created it (the mutex is owned by that thread).
    Public Sub Dispose() Implements IDisposable.Dispose
        stopSignal.Set()
        listener?.Join(2000)
        If owned Then instanceMutex.ReleaseMutex()
        instanceMutex?.Dispose() : showSignal?.Dispose() : stopSignal.Dispose()
    End Sub
End Class
