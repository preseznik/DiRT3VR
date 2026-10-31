Imports System.Drawing
Imports System.Text.Json

' Adapted from DiRT2VR. Separate storage prevents window close from saving edits.
Public Class WindowPreferences
    Public Property Version As Integer = 1
    Public Property Width As Integer
    Public Property Height As Integer
    Public Property Maximized As Boolean

    Public Shared Function Load(context As InstallContext) As WindowPreferences
        Try
            Dim path = IO.Path.Combine(context.UserRoot, "window-preferences.json")
            If File.Exists(path) Then
                Dim value = SafeFiles.ReadJson(Of WindowPreferences)(path)
                If value IsNot Nothing AndAlso value.Version = 1 AndAlso value.Width >= 100 AndAlso value.Width <= 10000 AndAlso value.Height >= 100 AndAlso value.Height <= 10000 Then Return value
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Optional preferences must not prevent startup or game recovery.
        End Try
        Return New WindowPreferences()
    End Function

    Public Function Fit(workArea As Size, dpi As Integer, fallback As Size) As Size
        Dim scale = Math.Clamp(dpi, 96, 768) / 96.0
        Dim w = If(Width >= 100 AndAlso Width <= 10000, CInt(Width * scale), fallback.Width)
        Dim h = If(Height >= 100 AndAlso Height <= 10000, CInt(Height * scale), fallback.Height)
        Return New Size(Math.Clamp(w, Math.Min(CInt(560 * scale), workArea.Width), workArea.Width),
                        Math.Clamp(h, Math.Min(CInt(540 * scale), workArea.Height), workArea.Height))
    End Function

    Public Shared Function Capture(normalSize As Size, dpi As Integer, maximized As Boolean) As WindowPreferences
        Dim scale = Math.Clamp(dpi, 96, 768) / 96.0
        Return New WindowPreferences With {.Width = CInt(normalSize.Width / scale), .Height = CInt(normalSize.Height / scale), .Maximized = maximized}
    End Function

    Public Sub Save(context As InstallContext)
        If Version <> 1 OrElse Width < 100 OrElse Width > 10000 OrElse Height < 100 OrElse Height > 10000 Then Return
        Try
            SafeFiles.SaveJson(IO.Path.Combine(context.UserRoot, "window-preferences.json"), Me)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Never block exit or recovery for optional window persistence.
        End Try
    End Sub
End Class
