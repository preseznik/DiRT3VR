Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

Module WheelGuardTests
    Private Const PrivateFlags As Reflection.BindingFlags = Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        TestScrolling(check)
        TestNestedScrolling(check)
        TestLifetime(check)
        TestLauncherEditors(root, check)
    End Sub

    ' Message values are passed directly to managed filters. No SendInput,
    ' SendMessage, PostMessage, focus changes, visible windows or real input.
    Private Function Wheel(target As Control, delta As Integer, Optional horizontal As Boolean = False) As Message
        Return Message.Create(target.Handle, If(horizontal, SettingsWheelGuard.MouseHWheel, SettingsWheelGuard.MouseWheel), New IntPtr(CLng(delta And &HFFFF) << 16), IntPtr.Zero)
    End Function

    Private Function Guarded(guard As SettingsWheelGuard, target As Control, delta As Integer, Optional horizontal As Boolean = False) As Boolean
        Dim message = Wheel(target, delta, horizontal)
        Return guard.PreFilterMessage(message)
    End Function

    Private Function TopOf(page As ScrollableControl) As Integer
        Return -page.AutoScrollPosition.Y
    End Function

    Private Function LeftOf(page As ScrollableControl) As Integer
        Return -page.AutoScrollPosition.X
    End Function

    Private Function NewPage(size As Size, content As Size) As Panel
        Return New Panel With {.AutoScroll = True, .Size = size, .AutoScrollMinSize = content}
    End Function

    Private Sub CreateOffscreenPage(page As ScrollableControl, check As Action(Of Boolean, String))
        ' AutoScrollPosition setters require Control.Created, not just a handle.
        ' Create only this hidden fixture subtree without showing its form.
        GetType(Control).GetMethod("CreateControl", PrivateFlags, Nothing, {GetType(Boolean)}, Nothing).Invoke(page, New Object() {True})
        page.PerformLayout()
        check(page.Created, "offscreen wheel fixture page is created")
    End Sub

    Private Sub TestScrolling(check As Action(Of Boolean, String))
        Using form As New LauncherForm(), page = NewPage(New Size(280, 160), New Size(1000, 1600))
            form.Controls.Add(page)
            Dim track As New TrackBar With {.Minimum = 0, .Maximum = 100, .Value = 40, .Size = New Size(180, 35)}
            Dim combo As New ComboBox With {.Top = 40, .Width = 180, .DropDownStyle = ComboBoxStyle.DropDownList}
            combo.Items.AddRange(New Object() {"Low", "Medium", "High"}) : combo.SelectedIndex = 1
            Dim number As New NumericUpDown With {.Top = 75, .Width = 100, .Value = 40}
            Dim text As New TextBox With {.Top = 110, .Width = 100, .Text = "Untouched"}
            page.Controls.AddRange(New Control() {track, combo, number, text})
            Dim created = form.Handle : CreateOffscreenPage(page, check)
            check(page.VerticalScroll.Visible AndAlso page.HorizontalScroll.Visible, "wheel fixture page scrolls both ways")
            Dim lines = 3, open = False
            Using guard As New SettingsWheelGuard(form, Function() lines, Function(c) open)
                Dim changes = 0
                AddHandler track.ValueChanged, Sub() changes += 1
                AddHandler combo.SelectedIndexChanged, Sub() changes += 1
                AddHandler number.ValueChanged, Sub() changes += 1
                page.AutoScrollPosition = New Point(40, 100)
                Dim y = TopOf(page), x = LeftOf(page), small = page.VerticalScroll.SmallChange
                Dim handled = Guarded(guard, track, -120)
                check(handled AndAlso TopOf(page) = y + 3 * small AndAlso LeftOf(page) = x, $"wheel over a slider scrolls its page: handled={handled}, y={y}->{TopOf(page)}, x={x}->{LeftOf(page)}, small={small}")
                check(Guarded(guard, combo, 120) AndAlso TopOf(page) = y AndAlso combo.SelectedIndex = 1, "wheel over a closed dropdown scrolls without changing its choice")
                check(Guarded(guard, number, -120) AndAlso TopOf(page) = y + 3 * small AndAlso number.Value = 40, "wheel over a number box scrolls its page")
                Dim child = number.Controls.OfType(Of TextBoxBase)().Single()
                y = TopOf(page)
                check(Guarded(guard, child, -120) AndAlso TopOf(page) = y + 3 * small, "a number box's inner edit window resolves to its editor")
                ' Fractional input is accumulated per page, not lost when the pointer crosses editors.
                y = TopOf(page)
                check(Guarded(guard, track, -30) AndAlso Guarded(guard, combo, -30) AndAlso Guarded(guard, child, -30) AndAlso TopOf(page) = y, "high-resolution deltas wait for a whole notch")
                check(Guarded(guard, number, -30) AndAlso TopOf(page) = y + 3 * small, "four quarter notches across editors scroll one notch")
                y = TopOf(page)
                check(Guarded(guard, track, -60) AndAlso Guarded(guard, combo, 60) AndAlso TopOf(page) = y, "opposite partial deltas cancel")
                check(Guarded(guard, track, -120) AndAlso TopOf(page) = y + 3 * small, "a whole notch after cancelled partials scrolls once")
                x = LeftOf(page) : y = TopOf(page)
                check(Guarded(guard, track, 120, horizontal:=True) AndAlso LeftOf(page) = x + 3 * page.HorizontalScroll.SmallChange AndAlso TopOf(page) = y, "horizontal wheel scrolls horizontally")
                lines = -1 : page.AutoScrollPosition = New Point(40, 100) : y = TopOf(page)
                check(Guarded(guard, track, -120) AndAlso TopOf(page) = y + page.VerticalScroll.LargeChange, "Windows' one screen per notch setting scrolls by page")
                lines = 0 : y = TopOf(page)
                check(Guarded(guard, track, -240) AndAlso TopOf(page) = y, "disabled Windows wheel scrolling still protects values")
                lines = 3
                check(Guarded(guard, track, Short.MinValue) AndAlso TopOf(page) = page.VerticalScroll.Maximum - page.VerticalScroll.LargeChange + 1, "large deltas clamp at the page end")
                check(Guarded(guard, track, 0) AndAlso track.Value = 40 AndAlso combo.SelectedIndex = 1 AndAlso number.Value = 40 AndAlso changes = 0, "no wheel message changed a value")
                ' The open-list branch is tested through a policy seam; opening a native
                ' dropdown here would display a popup, which offscreen tests must not do.
                open = True : y = TopOf(page)
                check(Not Guarded(guard, combo, -120) AndAlso TopOf(page) = y, "an open dropdown keeps wheel selection")
                open = False
                check(Guarded(guard, combo, -120), "a closed dropdown is protected again")
                check(Not Guarded(guard, text, -120), "text boxes keep native wheel handling")
                For Each id In {&H100, &H101, &H200, &H201, &H202}
                    Dim other = Message.Create(track.Handle, id, IntPtr.Zero, IntPtr.Zero)
                    check(Not guard.PreFilterMessage(other), "keyboard and mouse button messages are not filtered")
                Next
                track.Value = 41 : combo.SelectedIndex = 2 : number.Value = 41
                check(changes = 3, "ordinary value and selection changes still raise their events")
                Using foreign As New Form(), other As New TrackBar()
                    foreign.Controls.Add(other)
                    check(Not Guarded(guard, other, -120) AndAlso Not foreign.Visible, "another form's editors are left alone")
                End Using
                Dim missing = Message.Create(IntPtr.Zero, SettingsWheelGuard.MouseWheel, IntPtr.Zero, IntPtr.Zero)
                check(Not guard.PreFilterMessage(missing) AndAlso Not form.Visible, "unknown windows are ignored and no fixture window is shown")
            End Using
        End Using
    End Sub

    Private Sub TestNestedScrolling(check As Action(Of Boolean, String))
        Using form As New LauncherForm(), outer = NewPage(New Size(300, 180), New Size(0, 2200)), inner = NewPage(New Size(220, 100), New Size(0, 800))
            form.Controls.Add(outer) : outer.Controls.Add(inner)
            Dim track As New TrackBar With {.Maximum = 100, .Value = 50}
            inner.Controls.Add(track)
            Dim created = form.Handle : CreateOffscreenPage(outer, check) : CreateOffscreenPage(inner, check)
            Using guard As New SettingsWheelGuard(form, Function() 3)
                check(Guarded(guard, track, -120) AndAlso TopOf(inner) > 0 AndAlso TopOf(outer) = 0, "a nested slider scrolls its own page first")
                inner.AutoScrollPosition = New Point(0, 10000)
                Dim atEnd = TopOf(inner)
                check(Guarded(guard, track, -120) AndAlso TopOf(inner) = atEnd AndAlso TopOf(outer) > 0, "at the inner page end the outer page scrolls")
                inner.AutoScrollPosition = Point.Empty
                Dim outerTop = TopOf(outer)
                check(Guarded(guard, track, 120) AndAlso TopOf(inner) = 0 AndAlso TopOf(outer) < outerTop AndAlso track.Value = 50, "at the inner page start the outer page scrolls up")
                Dim standalone As New NumericUpDown With {.Value = 40}
                form.Controls.Add(standalone)
                check(Guarded(guard, standalone, -120) AndAlso standalone.Value = 40 AndAlso Not form.Visible, "an editor outside any scrolling page still keeps its value")
            End Using
        End Using
    End Sub

    Private Sub TestLifetime(check As Action(Of Boolean, String))
        Dim field = GetType(LauncherForm).GetField("wheelGuard", PrivateFlags)
        Dim form As New LauncherForm(), track As New TrackBar With {.Maximum = 100, .Value = 50}
        form.Controls.Add(track)
        Dim installed = DirectCast(field.GetValue(form), SettingsWheelGuard)
        Try
            Dim message = Wheel(track, -120)
            check(Application.FilterMessage(message) AndAlso track.Value = 50, "every launcher window installs wheel protection")
            installed.Dispose()
            message = Wheel(track, -120)
            check(Not Application.FilterMessage(message) AndAlso installed.IsDisposed, "a disposed guard stops filtering")
        Finally
            form.Dispose()
        End Try
        Dim another As New LauncherForm()
        Dim automatic = DirectCast(field.GetValue(another), SettingsWheelGuard)
        another.Dispose()
        check(automatic.IsDisposed, "disposing a launcher window removes its wheel filter")
    End Sub

    Private Sub TestLauncherEditors(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "wheel-ui")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "wheel-users"), Path.Combine(game, "graphics.xml"), root)
        For Each modernStyle In {False, True}
            Call (New LauncherSettings With {.ModernInterface = modernStyle}).Save(ctx)
            Using form As New MainForm(ctx)
                Dim guard = DirectCast(GetType(LauncherForm).GetField("wheelGuard", PrivateFlags).GetValue(form), SettingsWheelGuard)
                Dim editors = Descendants(form).Where(AddressOf SettingsWheelGuard.IsValueEditor).ToArray()
                For Each editor In editors
                    Dim before = EditorValue(editor), changes = 0
                    Dim changed As EventHandler = Sub() changes += 1
                    Subscribe(editor, changed, True)
                    Try
                        For Each delta In {-120, 120, -30, -30, -30, -30, 240}
                            check(Guarded(guard, editor, delta) AndAlso Guarded(guard, editor, delta, horizontal:=True), $"launcher wheel filter handles {editor.Name}")
                        Next
                        check(Object.Equals(before, EditorValue(editor)) AndAlso changes = 0, $"wheel leaves {editor.Name} unchanged, modern={modernStyle}")
                    Finally
                        Subscribe(editor, changed, False)
                    End Try
                Next
                Dim names = editors.Select(Function(e) e.Name).ToArray()
                check(names.Contains("VrRenderScaleSlider") AndAlso names.Contains("RuntimeChoice") AndAlso names.Contains("DesktopResolution") AndAlso names.Contains("SettingsProfileChoice") AndAlso editors.Length >= 6,
                      $"every launcher slider and dropdown is protected: {String.Join(", ", names)}")
                check(Not form.HasUnsavedSettings() AndAlso Not form.Visible, $"wheel input leaves launcher settings unchanged, modern={modernStyle}")
            End Using
        Next
    End Sub

    Private Function EditorValue(editor As Control) As Object
        If TypeOf editor Is TrackBar Then Return DirectCast(editor, TrackBar).Value
        If TypeOf editor Is ComboBox Then Return DirectCast(editor, ComboBox).SelectedIndex
        Return DirectCast(editor, NumericUpDown).Value
    End Function

    Private Sub Subscribe(editor As Control, handler As EventHandler, attach As Boolean)
        If TypeOf editor Is TrackBar Then
            If attach Then AddHandler DirectCast(editor, TrackBar).ValueChanged, handler Else RemoveHandler DirectCast(editor, TrackBar).ValueChanged, handler
        ElseIf TypeOf editor Is ComboBox Then
            If attach Then AddHandler DirectCast(editor, ComboBox).SelectedIndexChanged, handler Else RemoveHandler DirectCast(editor, ComboBox).SelectedIndexChanged, handler
        Else
            If attach Then AddHandler DirectCast(editor, NumericUpDown).ValueChanged, handler Else RemoveHandler DirectCast(editor, NumericUpDown).ValueChanged, handler
        End If
    End Sub

    Private Iterator Function Descendants(parent As Control) As IEnumerable(Of Control)
        For Each child As Control In parent.Controls
            Yield child
            For Each nested In Descendants(child)
                Yield nested
            Next
        Next
    End Function
End Module
