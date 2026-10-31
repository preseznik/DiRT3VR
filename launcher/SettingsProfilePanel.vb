Imports System.Drawing
Imports System.Windows.Forms

Public Class SettingsProfilePanel
    Inherits VerticalStack
    Private store As SettingsProfileStore
    Private ReadOnly currentSettings As Func(Of LauncherSettings)
    Private ReadOnly loadProfile As Func(Of SettingsProfile, Boolean)
    Private ReadOnly choices As New ComboBox With {.Name = "SettingsProfileChoice", .DropDownStyle = ComboBoxStyle.DropDownList, .AccessibleName = "Saved settings profiles"}
    Private ReadOnly profileName As New TextBox With {.Name = "SettingsProfileName", .MaxLength = 64, .AccessibleName = "Settings profile name"}
    Private ReadOnly message As New Label With {.Name = "SettingsProfileMessage", .AutoSize = True, .UseMnemonic = False}
    Private ReadOnly loadButton As New Button With {.Name = "LoadSettingsProfile", .Text = "Load", .AutoSize = True}
    Private ReadOnly updateButton As New Button With {.Name = "UpdateSettingsProfile", .Text = "Update selected…", .AutoSize = True}
    Private ReadOnly renameButton As New Button With {.Name = "RenameSettingsProfile", .Text = "Rename", .AutoSize = True}
    Private ReadOnly deleteButton As New Button With {.Name = "DeleteSettingsProfile", .Text = "Delete…", .AutoSize = True}
    Private ReadOnly exportButton As New Button With {.Name = "ExportSettingsProfile", .Text = "Export…", .AutoSize = True}

    Public Sub New(context As InstallContext, current As Func(Of LauncherSettings), apply As Func(Of SettingsProfile, Boolean))
        Name = "SettingsProfiles" : Margin = New Padding(0)
        currentSettings = current : loadProfile = apply
        Controls.Add(New Label With {.Text = "Save desktop, VR, startup, appearance and logging choices. Loading changes the controls; Save settings or Launch keeps those choices. Game saves and local paths stay separate.", .AutoSize = True})
        Field(Me, "Saved profile", choices) : StyleChoice(choices, Me)
        Field(Me, "Name", profileName)
        Dim create As New Button With {.Name = "CreateSettingsProfile", .Text = "Save as new", .AutoSize = True}
        Dim importButton As New Button With {.Name = "ImportSettingsProfile", .Text = "Import…", .AutoSize = True}
        Dim refresh As New Button With {.Name = "RefreshSettingsProfiles", .Text = "Refresh", .AutoSize = True}
        Dim commands As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = True, .Margin = New Padding(0, 8, 0, 8)}
        commands.Controls.AddRange({loadButton, create, updateButton, renameButton, deleteButton, importButton, exportButton, refresh})
        Controls.Add(commands) : Controls.Add(message)
        AddHandler choices.SelectedIndexChanged, Sub()
                                                     Dim selected = Selection()
                                                     profileName.Text = If(selected?.Profile?.Name, "")
                                                     message.Text = If(selected?.Problem, "Selecting a profile does not load it. Export uses the selected saved profile.")
                                                     UpdateButtons()
                                                 End Sub
        AddHandler create.Click, Sub() Run(Sub()
                                              Dim added = store.Create(profileName.Text, ProfileValues.Capture(currentSettings()))
                                              Reload(added.Id)
                                              message.Text = "Saved profile: " & added.Profile.Name & ". Current launcher preferences were not overwritten."
                                          End Sub)
        AddHandler loadButton.Click, Sub() Run(Sub()
                                                  Dim selected = Selection()
                                                  If selected Is Nothing Then Return
                                                  Dim fresh = store.Read(selected.Id)
                                                  If loadProfile(fresh.Profile) Then
                                                      message.Text = "Loaded " & fresh.Profile.Name & ". Use Save settings or Launch to keep these choices."
                                                  End If
                                              End Sub)
        AddHandler updateButton.Click, Sub() Run(Sub()
                                                    Dim selected = Selection()
                                                    If selected?.Profile Is Nothing Then Return
                                                    If Not Confirm("Replace the settings saved in " & ChrW(34) & selected.Profile.Name & ChrW(34) & " with the current controls?") Then Return
                                                    Dim saved = store.Update(selected, selected.Profile.Name, ProfileValues.Capture(currentSettings()))
                                                    Reload(saved.Id) : message.Text = "Updated " & saved.Profile.Name & "."
                                                End Sub)
        AddHandler renameButton.Click, Sub() Run(Sub()
                                                    Dim selected = Selection()
                                                    If selected?.Profile Is Nothing Then Return
                                                    Dim renamed = store.Update(selected, profileName.Text, selected.Profile.Settings)
                                                    Reload(renamed.Id) : message.Text = "Profile renamed. Its saved settings are unchanged."
                                                End Sub)
        AddHandler deleteButton.Click, Sub() Run(Sub()
                                                    Dim selected = Selection()
                                                    If selected?.Profile Is Nothing Then Return
                                                    If Not Confirm("Delete settings profile " & ChrW(34) & selected.Profile.Name & ChrW(34) & "? Current controls and game saves will stay unchanged.") Then Return
                                                    store.Delete(selected) : Reload()
                                                    message.Text = "Settings profile deleted."
                                                End Sub)
        AddHandler importButton.Click, Sub() Run(Sub()
                                                    Using picker As New OpenFileDialog With {.Title = "Import DiRT3VR settings profile", .Filter = "DiRT3VR settings profile (*.json)|*.json", .CheckFileExists = True}
                                                        If picker.ShowDialog(FindForm()) <> DialogResult.OK Then Return
                                                        Dim added = store.Import(picker.FileName)
                                                        Reload(added.Id) : message.Text = "Imported " & added.Profile.Name & ". Click Load to use it."
                                                    End Using
                                                End Sub)
        AddHandler exportButton.Click, Sub() Run(Sub()
                                                    Dim selected = Selection()
                                                    If selected?.Profile Is Nothing Then Return
                                                    Using picker As New SaveFileDialog With {.Title = "Export saved settings profile", .Filter = "DiRT3VR settings profile (*.json)|*.json", .FileName = "DiRT3VR-settings-profile.json", .DefaultExt = "json", .AddExtension = True, .OverwritePrompt = True}
                                                        If picker.ShowDialog(FindForm()) <> DialogResult.OK Then Return
                                                        store.Export(selected, picker.FileName)
                                                        message.Text = "Exported " & selected.Profile.Name & ". Local paths were excluded."
                                                    End Using
                                                End Sub)
        AddHandler refresh.Click, Sub() Reload(Selection()?.Id)
        SetContext(context)
    End Sub

    Public Sub SetContext(context As InstallContext)
        store = New SettingsProfileStore(context)
        Reload()
    End Sub

    Private Function Selection() As SettingsProfileEntry
        Return TryCast(choices.SelectedItem, SettingsProfileEntry)
    End Function

    Private Sub UpdateButtons()
        Dim valid = Selection()?.Profile IsNot Nothing
        For Each button In {loadButton, updateButton, renameButton, deleteButton, exportButton}
            button.Enabled = valid
        Next
    End Sub

    Public Sub Reload(Optional selectedId As String = Nothing)
        Run(Sub()
                choices.Items.Clear() : profileName.Clear() : UpdateButtons()
                For Each entry In store.List()
                    choices.Items.Add(entry)
                    If entry.Id = selectedId Then choices.SelectedItem = entry
                Next
                message.Text = If(choices.Items.Count = 0, "No saved settings profiles. Enter a name and choose Save as new.", If(Selection()?.Problem, "Select a saved profile, then click Load to use it."))
            End Sub)
    End Sub

    Private Function Confirm(text As String) As Boolean
        Return MessageBox.Show(FindForm(), text, "Settings profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) = DialogResult.Yes
    End Function

    Private Sub Run(action As Action)
        Try
            action()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Text.Json.JsonException
            message.Text = ex.Message
        End Try
    End Sub

    Protected Overrides Sub OnSizeChanged(e As EventArgs)
        If message IsNot Nothing Then message.MaximumSize = New Size(Math.Max(1, ClientSize.Width - message.Margin.Horizontal), 0)
        MyBase.OnSizeChanged(e)
    End Sub
End Class
