Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Threading
Imports System.Windows.Forms

Module BackgroundTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' Closing the window keeps DiRT3VR running only while something needs it, and never blocks Windows.
        check(BackgroundHost.KeepsRunningOnClose(True, CloseReason.UserClosing), "closing during a session keeps DiRT3VR running")
        check(Not BackgroundHost.KeepsRunningOnClose(False, CloseReason.UserClosing), "closing without a session quits")
        check(Not BackgroundHost.KeepsRunningOnClose(True, CloseReason.WindowsShutDown) AndAlso Not BackgroundHost.KeepsRunningOnClose(True, CloseReason.TaskManagerClosing),
              "sign-out, shutdown and Task Manager are not blocked by a session")
        check(BackgroundHost.QuitsAfterSession(False, False), "after the files are restored in the background, DiRT3VR quits")
        check(Not BackgroundHost.QuitsAfterSession(True, False) AndAlso Not BackgroundHost.QuitsAfterSession(False, True), "pending recovery or an error shows the window instead")

        ' One launcher per sign-in; a second start signals the first.
        Dim name = "DiRT3VR.Test." & Guid.NewGuid().ToString("N")
        Dim signals = 0
        Using shown As New ManualResetEventSlim(False)
            Using first As New SingleInstance(name)
                check(first.Primary, "the first launcher owns the instance")
                first.Listen(Sub()
                                 Interlocked.Increment(signals)
                                 shown.Set()
                             End Sub)
                Using second As New SingleInstance(name)
                    check(Not second.Primary, "a second launcher finds the running one")
                    second.SignalPrimary()
                End Using
                check(shown.Wait(5000) AndAlso signals = 1, "the running launcher is asked to show its window")
                shown.Reset()
                Using third As New SingleInstance(name)
                    third.SignalPrimary()
                End Using
                check(shown.Wait(5000) AndAlso signals = 2, "each later start asks again")
            End Using
        End Using
        Using afterwards As New SingleInstance(name)
            check(afterwards.Primary, "once the launcher quits, the next start is the primary again")
            afterwards.SignalPrimary()
        End Using
        check(signals = 2, "a closed launcher no longer listens")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        ' The notification-area icon and its menu, with a stand-in window.
        Using owner As New Form With {.ShowInTaskbar = False, .StartPosition = FormStartPosition.Manual, .Location = New Point(-30000, -30000), .Icon = DirectCast(SystemIcons.Application.Clone(), Icon)}
            owner.Show() : Application.DoEvents()
            Dim opened = 0, stopped = 0, ended = 0
            Dim vr As Boolean? = True
            Using host As New BackgroundHost(owner, Sub() opened += 1, Sub() stopped += 1, Sub() ended += 1, Function() vr)
                check(Not host.Active AndAlso Not host.IconVisible, "no icon until the window is closed during a session")
                host.Enter("Stereo scene frames submitted", "DiRT3VR restores the original game files when DiRT 3 closes, then quits.")
                Application.DoEvents()
                check(host.Active AndAlso host.IconVisible AndAlso Not owner.Visible, "closing hides the window and shows the icon")
                check(host.IconText = "DiRT3VR · Stereo scene frames submitted", "the icon's tooltip shows the session status (" & host.IconText & ")")
                host.UpdateStatus(New String("x"c, 300))
                check(host.IconText.Length <= 127, "the tooltip stays within Windows' limit")
                Dim item = Function(key As String) DirectCast(host.MenuItems(key), ToolStripMenuItem)
                check(host.MenuItems.Count = 4 AndAlso item("BackgroundOpen").Font.Bold, "the menu offers Open (default), Stop VR and End session")
                check(item("BackgroundStopVr").Available AndAlso item("BackgroundStopVr").Enabled, "Stop VR is offered while VR is on")
                item("BackgroundStopVr").PerformClick() : item("BackgroundEndSession").PerformClick() : item("BackgroundOpen").PerformClick()
                check(stopped = 1 AndAlso ended = 1 AndAlso opened = 1, "the menu items call Stop VR, End session and Open")
                vr = False : host.RefreshMenu()
                check(item("BackgroundStopVr").Available AndAlso Not item("BackgroundStopVr").Enabled AndAlso item("BackgroundStopVr").Text.StartsWith("VR stopped", StringComparison.Ordinal), "after Stop VR the item says VR is off")
                vr = Nothing : host.RefreshMenu()
                check(Not item("BackgroundStopVr").Available AndAlso item("BackgroundEndSession").Available, "desktop sessions offer only End session")
                host.Leave() : Application.DoEvents()
                check(Not host.Active AndAlso Not host.IconVisible AndAlso owner.Visible, "Open brings the window back and removes the icon")
            End Using
        End Using

        ' Footer: Stop VR sits next to the VR button and only takes space while VR runs.
        Using form As New Form With {.ShowInTaskbar = False, .StartPosition = FormStartPosition.Manual, .Location = New Point(-30000, -30000), .Font = New Font("Segoe UI", 10)}
            Dim buttons = {"Launch", "End session", "Save settings", "Restore original files", "Open logs", "Stop VR"}.Select(Function(text) New Button With {.Text = text, .AutoSize = True}).ToArray()
            buttons(5).Visible = False
            Dim footer As New LauncherFooter(buttons(0), buttons(1), buttons(2), buttons(3), buttons(4), buttons(5)) With {.Dock = DockStyle.None}
            form.Controls.Add(footer)
            form.Show() : Application.DoEvents()
            For Each width In {900, 640, 520}
                For Each running In {False, True}
                    buttons(5).Visible = running
                    footer.Width = width : footer.PerformLayout() : Application.DoEvents()
                    Dim shown = buttons.Where(Function(b) b.Visible).ToArray()
                    Dim overlap = shown.Any(Function(a) shown.Any(Function(b) a IsNot b AndAlso a.Bounds.IntersectsWith(b.Bounds)))
                    Dim inside = shown.All(Function(b) b.Left >= 0 AndAlso b.Right <= footer.Width)
                    check(Not overlap AndAlso inside, $"footer buttons fit without overlapping (width {width}, VR running {running})")
                    If running Then check(buttons(5).Left > buttons(1).Right AndAlso buttons(5).Top = buttons(1).Top, $"Stop VR follows the VR button (width {width})")
                Next
            Next
            form.Close()
        End Using

        ' The launcher window: no session means Close quits; reopening restores a minimized window.
        Dim ctx = Context(root, "background-ui")
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim stopButton = form.Controls.Find("StopVR", True).Single()
            check(Not stopButton.Visible AndAlso form.Controls.Find("LaunchVR", True).Single().Text = "Launch VR", "Stop VR appears only during VR")
            form.WindowState = FormWindowState.Minimized : Application.DoEvents()
            form.ShowFromBackground() : Application.DoEvents()
            check(form.Visible AndAlso form.WindowState = FormWindowState.Normal, "starting DiRT3VR again restores the running window")
            form.Close() : Application.DoEvents()
            check(form.IsDisposed, "without a session, closing the window quits")
        End Using
    End Sub
End Module
