Imports System.Drawing
Imports System.Globalization
Imports System.Text.Json

' GRID2VR model: scene scale is relative to the runtime recommendation;
' output stays at that recommendation. DiRT3 keeps tighter x86 resource limits.
Public Class HeadsetResolution
    Public ReadOnly Property RuntimeMode As String
    Public ReadOnly Property Headset As String
    Public ReadOnly Property Width As Integer
    Public ReadOnly Property Height As Integer
    Public Const MaximumDimension As Integer = 4096

    Public Sub New(mode As String, name As String, w As Integer, h As Integer)
        If Not VrRuntime.Modes.Contains(mode) OrElse String.IsNullOrWhiteSpace(name) OrElse name.Length > 256 Then Throw New IOException("Invalid headset identity.")
        ValidateSceneSize(w, h)
        RuntimeMode = mode : Headset = name : Width = w : Height = h
    End Sub

    Public Shared Sub ValidateSceneSize(w As Integer, h As Integer)
        If w < 320 OrElse h < 180 OrElse w > MaximumDimension OrElse h > MaximumDimension Then Throw New IOException("Resolution exceeds this renderer's limits (320×180 minimum, 4096 per dimension). Lower VR render scale or the runtime's recommended resolution.")
    End Sub

    Public Function SceneSize(scale As Integer) As Size
        If scale < 50 OrElse scale > 300 Then Throw New IOException("Choose a VR render scale between 50% and 300%.")
        Dim size As New Size(Width * scale \ 100, Height * scale \ 100)
        ValidateSceneSize(size.Width, size.Height)
        Return size
    End Function

    ' The scene size with the VR field of view cropped (#16a86c75, Lens's contract): each axis times its kept share,
    ' after the render scale, in whole pixels.
    Public Function CroppedSceneSize(scale As Integer, widthPercent As Integer, heightPercent As Integer) As Size
        If widthPercent < LauncherSettings.MinimumVrFieldOfView OrElse widthPercent > 100 OrElse heightPercent < LauncherSettings.MinimumVrFieldOfView OrElse heightPercent > 100 Then Throw New IOException("Choose a VR field of view from 70% to 100%.")
        Dim size = SceneSize(scale)
        Dim cropped As New Size(size.Width * widthPercent \ 100, size.Height * heightPercent \ 100)
        ValidateSceneSize(cropped.Width, cropped.Height)
        Return cropped
    End Function

    Public ReadOnly Property MaximumScale As Integer
        Get
            Return Math.Min(300, Math.Min(((MaximumDimension + 1) * 100 - 1) \ Width, ((MaximumDimension + 1) * 100 - 1) \ Height))
        End Get
    End Property

    ' The scale a VR session uses (#7f93667d): the chosen one, or the largest this headset's recommended
    ' size allows within the renderer's 4096 per dimension, rather than refusing the launch.
    Public Function EffectiveScale(scale As Integer) As Integer
        If scale < 50 OrElse scale > 300 Then Throw New IOException("Choose a VR render scale between 50% and 300%.")
        Return Math.Min(scale, MaximumScale)
    End Function

    ' For the launch message: why the session runs at a lower scale than chosen, or "".
    Public Function ScaleNote(scale As Integer) As String
        Dim used = EffectiveScale(scale)
        Return If(used < scale, $"VR render scale {scale}% is above the {used}% this headset's {Width} × {Height} allows (4096 per dimension), so this session uses {used}%.", "")
    End Function

    Public Function Summary(scale As Integer, Optional widthPercent As Integer = 100, Optional heightPercent As Integer = 100) As String
        Dim text = $"{Headset} · {If(RuntimeMode = "other", VrRuntime.OtherName, VrRuntime.Label(RuntimeMode))}" & Environment.NewLine &
            $"Output: {Width} × {Height} per eye (100% baseline)." & Environment.NewLine
        Try
            Dim used = EffectiveScale(scale)
            Dim size = CroppedSceneSize(used, widthPercent, heightPercent)
            Dim cropped = If(widthPercent < 100 OrElse heightPercent < 100, $" with a {If(widthPercent = heightPercent, $"{heightPercent}%", $"{widthPercent}% × {heightPercent}%")} field of view", "")
            text &= $"Render: {size.Width} × {size.Height} per eye at {used}%{cropped}." & If(used < scale, $" {scale}% is above this headset's maximum, so Launch VR uses {used}%.", "")
        Catch ex As IOException
            text &= "Requested render size is unsupported. Lower VR render scale."
        End Try
        Return text & Environment.NewLine & $"Maximum supported for this headset: {MaximumScale}%."
    End Function

    Public Shared Function FromReport(report As JsonElement, mode As String) As HeadsetResolution
        Try
            If report.GetProperty("sessionCreated").ValueKind <> JsonValueKind.False Then Throw New IOException("The headset query must not create a VR session.")
            Dim runtime = report.GetProperty("runtimeName").GetString()
            If (mode = "vdxr" AndAlso runtime <> "VirtualDesktopXR") OrElse
               (mode = "steamvr" AndAlso (runtime Is Nothing OrElse Not runtime.StartsWith("SteamVR/", StringComparison.Ordinal))) OrElse
               (mode = "other" AndAlso String.IsNullOrWhiteSpace(runtime)) Then Throw New IOException("Headset query returned a different OpenXR runtime.")
            Dim views = report.GetProperty("views")
            If views.GetArrayLength() <> 2 Then Throw New IOException("Headset query must report both eyes.")
            Dim w = views(0).GetProperty("width").GetInt32(), h = views(0).GetProperty("height").GetInt32()
            For Each eye In views.EnumerateArray()
                If eye.GetProperty("width").GetInt32() <> w OrElse eye.GetProperty("height").GetInt32() <> h Then Throw New IOException("This renderer requires matching recommended dimensions for both eyes.")
                If eye.GetProperty("maxWidth").GetInt32() < w OrElse eye.GetProperty("maxHeight").GetInt32() < h Then Throw New IOException("Headset recommendation exceeds its OpenXR image limits.")
            Next
            Return New HeadsetResolution(mode, report.GetProperty("systemName").GetString(), w, h)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is KeyNotFoundException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is OverflowException
            Throw New IOException("Headset query returned an invalid resolution report.", ex)
        End Try
    End Function
End Class
