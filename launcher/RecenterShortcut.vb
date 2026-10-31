Imports System.Windows.Forms

' The recenter key the VR renderer reads from DIRT3VR_RECENTER_KEY as "key modifiers": a Windows
' virtual-key code plus Ctrl=1, Alt=2, Shift=4 (the encoding DiRT2VR and GRID2VR use).
' Keep Valid identical to RecenterShortcut::Valid in src/recenter_shortcut.h.
Public NotInheritable Class RecenterShortcut
    Public Const DefaultKey As Integer = 121 ' F10

    Private Sub New()
    End Sub

    Public Shared Function ValidKey(key As Integer) As Boolean
        Return (key >= 48 AndAlso key <= 57) OrElse (key >= 65 AndAlso key <= 90) OrElse (key >= 96 AndAlso key <= 135) OrElse
            key = 8 OrElse key = 9 OrElse key = 13 OrElse key = 19 OrElse key = 32 OrElse (key >= 33 AndAlso key <= 40) OrElse key = 45 OrElse key = 46
    End Function

    ' Alt+Tab, Alt+F4 and Ctrl+Alt+Delete stay with Windows.
    Public Shared Function Valid(key As Integer, modifiers As Integer) As Boolean
        Return ValidKey(key) AndAlso modifiers >= 0 AndAlso modifiers <= 7 AndAlso
            Not ((modifiers And 2) <> 0 AndAlso (key = 9 OrElse key = 115)) AndAlso Not (key = 46 AndAlso (modifiers And 3) = 3)
    End Function

    Public Shared Function Describe(key As Integer, modifiers As Integer) As String
        Return If((modifiers And 1) <> 0, "Ctrl+", "") & If((modifiers And 2) <> 0, "Alt+", "") & If((modifiers And 4) <> 0, "Shift+", "") & KeyName(key)
    End Function

    Public Shared Function Modifiers(keyData As Keys) As Integer
        Return If((keyData And Keys.Control) <> 0, 1, 0) Or If((keyData And Keys.Alt) <> 0, 2, 0) Or If((keyData And Keys.Shift) <> 0, 4, 0)
    End Function

    ' A press of Ctrl, Alt, Shift or Windows on its own: keep waiting for the main key.
    Public Shared Function IsModifierOnly(keyData As Keys) As Boolean
        Dim key = keyData And Keys.KeyCode
        Return key = Keys.ControlKey OrElse key = Keys.ShiftKey OrElse key = Keys.Menu OrElse key = Keys.LWin OrElse key = Keys.RWin OrElse
            key = Keys.LControlKey OrElse key = Keys.RControlKey OrElse key = Keys.LShiftKey OrElse key = Keys.RShiftKey OrElse key = Keys.LMenu OrElse key = Keys.RMenu
    End Function

    Private Shared Function KeyName(key As Integer) As String
        Select Case key
            Case 48 To 57, 65 To 90 : Return ChrW(key).ToString()
            Case 96 To 105 : Return "Num " & (key - 96).ToString(Globalization.CultureInfo.InvariantCulture)
            Case 112 To 135 : Return "F" & (key - 111).ToString(Globalization.CultureInfo.InvariantCulture)
            Case 8 : Return "Backspace"
            Case 9 : Return "Tab"
            Case 13 : Return "Enter"
            Case 19 : Return "Pause"
            Case 32 : Return "Space"
            Case 33 : Return "Page Up"
            Case 34 : Return "Page Down"
            Case 35 : Return "End"
            Case 36 : Return "Home"
            Case 37 : Return "Left"
            Case 38 : Return "Up"
            Case 39 : Return "Right"
            Case 40 : Return "Down"
            Case 45 : Return "Insert"
            Case 46 : Return "Delete"
            Case 106 : Return "Num *"
            Case 107 : Return "Num +"
            Case 109 : Return "Num -"
            Case 110 : Return "Num ."
            Case 111 : Return "Num /"
            Case Else : Return "Key " & key.ToString(Globalization.CultureInfo.InvariantCulture)
        End Select
    End Function
End Class
