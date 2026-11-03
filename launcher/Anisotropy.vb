Imports System.Globalization

' Anisotropic filtering (#15714dd2), as in DiRT2VR. DiRT 3 has no setting for it, so Wrench's renderer overrides
' the game's sampler states (src/anisotropic_filter.*). Contract: DIRT3VR_ANISOTROPY = 1 (off: trilinear) / 2 / 4 /
' 8 / 16; unset or 0 keeps the game's own filtering. The renderer needs DIRT3VR_DIAGNOSTICS=1, which VR sessions and
' the desktop sessions that install the renderer set.
Public NotInheritable Class Anisotropy
    Public Const VariableName As String = "DIRT3VR_ANISOTROPY"
    ' The choices in LauncherSettings.AnisotropyLevels order.
    Public Shared ReadOnly Names As String() = {"Use game setting", "Off", "2×", "4×", "8×", "16×"}

    Private Sub New()
    End Sub

    ' Passes a choice other than the game's; the game's own removes the variable.
    Public Shared Sub Configure(start As ProcessStartInfo, level As Integer)
        If Not LauncherSettings.AnisotropyLevels.Contains(level) Then Throw New IOException("Choose Use game setting, Off, 2×, 4×, 8× or 16× anisotropic filtering.")
        If level = 0 Then
            start.Environment.Remove(VariableName)
        Else
            start.Environment(VariableName) = level.ToString(CultureInfo.InvariantCulture)
        End If
    End Sub

    Public Shared Function Describe(level As Integer) As String
        Dim index = Array.IndexOf(LauncherSettings.AnisotropyLevels, level)
        Return If(index < 0, "", Names(index))
    End Function
End Class
