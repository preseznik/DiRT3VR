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
        If (mode <> "vdxr" AndAlso mode <> "steamvr") OrElse String.IsNullOrWhiteSpace(name) OrElse name.Length > 256 Then Throw New IOException("Invalid headset identity.")
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

    Public ReadOnly Property MaximumScale As Integer
        Get
            Return Math.Min(300, Math.Min(((MaximumDimension + 1) * 100 - 1) \ Width, ((MaximumDimension + 1) * 100 - 1) \ Height))
        End Get
    End Property

    Public Function Summary(scale As Integer) As String
        Dim text = $"{Headset} · {If(RuntimeMode = "vdxr", "VDXR", "SteamVR")}" & Environment.NewLine &
            $"Output: {Width} × {Height} per eye (100% baseline)." & Environment.NewLine
        Try
            Dim size = SceneSize(scale)
            text &= $"Render: {size.Width} × {size.Height} per eye at {scale}%."
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
               (mode = "steamvr" AndAlso (runtime Is Nothing OrElse Not runtime.StartsWith("SteamVR/", StringComparison.Ordinal))) Then Throw New IOException("Headset query returned a different OpenXR runtime.")
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
