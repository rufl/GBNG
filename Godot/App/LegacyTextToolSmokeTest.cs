using Godot;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunTextToolSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(3);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The TextTool smoke requires an open project in the editor.");

            _textToolStylePicker.Select(0);
            SetTextToolActive(true);
            Vector2 billboardPlacementScreen = _camera.UnprojectPosition(Vector3.Zero);
            UpdateTextToolPreview(billboardPlacementScreen);
            if (!_textToolHasPlacement || _textToolPreview is not { Visible: true })
                throw new InvalidOperationException("The Text tool did not show a world-space placement preview.");
            Vector3 billboardPosition = _textToolPlacementPosition;
            if (billboardPosition.DistanceTo(Vector3.Up) > 0.25f)
                throw new InvalidOperationException($"The Text tool placed the preview at an unexpected stage position: {billboardPosition}.");

            int historyBeforeCreate = _historyPosition;
            _UnhandledInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = billboardPlacementScreen
            });
            WorldActorNode billboardPanel = _textToolEditingActor
                ?? throw new InvalidOperationException("Clicking the world did not create and edit a BillboardTextPanel.");
            if (!billboardPanel.IsBillboardTextPanel
                || !billboardPanel.Scale.IsEqualApprox(Vector3.One * 6f)
                || billboardPanel.State.IsSolid
                || billboardPanel.State.EnablePhysics
                || billboardPanel.State.EnableGravity
                || !billboardPanel.GlobalPosition.IsEqualApprox(billboardPosition)
                || !_textToolEditorOverlay.Visible
                || _textToolEditField.Text != TextToolPlaceholder)
                throw new InvalidOperationException("The placed billboard did not preserve legacy TextTool defaults or open its editor.");
            const string placedText = "Placed with the Text tool";
            _textToolEditField.Text = placedText;
            UpdateTextToolEditorText();
            HandleTextToolEditorInput(new InputEventKey { Keycode = Key.Enter, Pressed = true, ShiftPressed = true });
            if (_textToolEditingActor != billboardPanel || !_textToolEditorOverlay.Visible)
                throw new InvalidOperationException("Shift+Enter should keep the panel editor open for another line.");
            await WaitForUiFrames(2);
            if (billboardPanel.State.CommentText != placedText
                || billboardPanel.CommentPanelRenderer?.CommentText != placedText)
                throw new InvalidOperationException(
                    $"Typing did not update the new panel's serialized and rendered text live (state='{billboardPanel.State.CommentText}', renderer='{billboardPanel.CommentPanelRenderer?.CommentText}').");
            string screenshotPath = ProjectSettings.GlobalizePath("res://.godot/text-tool-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
            if (GetViewport().GetTexture().GetImage().SavePng(screenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not save the Text tool UI screenshot.");
            HandleTextToolEditorInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
            if (_textToolEditingActor is not null
                || _textToolEditorOverlay.Visible
                || _historyPosition != historyBeforeCreate + 1
                || billboardPanel.State.CommentText != placedText)
                throw new InvalidOperationException("Enter did not finish the new panel as a single undoable creation.");

            string billboardName = billboardPanel.LegacyName;
            UndoEdit();
            if (FindActor(billboardName) is not null)
                throw new InvalidOperationException("Undo did not remove the text panel created by the tool.");
            RedoEdit();
            billboardPanel = FindActor(billboardName)
                ?? throw new InvalidOperationException("Redo did not restore the placed text panel.");
            if (billboardPanel.State.CommentText != placedText)
                throw new InvalidOperationException("Redo did not restore the placed panel text.");

            Vector2 hoverScreen = _camera.UnprojectPosition(billboardPanel.GetWorldRenderBounds().GetCenter());
            UpdateTextToolPreview(hoverScreen);
            if (_textToolHoverActor != billboardPanel || !_textToolHoverHint.Visible)
                throw new InvalidOperationException("Hovering the placed panel did not identify it with visible edit feedback.");
            await WaitForUiFrames(2);
            if (_textToolHoverHint.Size.X < 180f || _textToolHoverHint.Size.Y < 24f)
                throw new InvalidOperationException($"The visible hover hint has no rendered size ({_textToolHoverHint.Size}).");
            string hoverScreenshotPath = ProjectSettings.GlobalizePath("res://.godot/text-tool-hover-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(hoverScreenshotPath) != Error.Ok)
                throw new InvalidOperationException("Could not save the Text tool hover-feedback screenshot.");
            int historyBeforeEdit = _historyPosition;
            HandleTextToolClick(hoverScreen);
            if (_textToolEditingActor != billboardPanel || _textToolEditField.Text != placedText || _textToolHoverHint.Visible)
                throw new InvalidOperationException("Clicking an existing panel did not open its text editor and clear the hover hint.");
            const string editedText = "Edited by hovering";
            _textToolEditField.Text = editedText;
            FinishTextToolEditing();
            if (billboardPanel.State.CommentText != editedText || _historyPosition != historyBeforeEdit + 1)
                throw new InvalidOperationException("Finishing a hover edit did not update text as one undoable operation.");
            UndoEdit();
            if (billboardPanel.State.CommentText != placedText)
                throw new InvalidOperationException("Undo did not restore the original hover-edited text.");
            RedoEdit();
            if (billboardPanel.State.CommentText != editedText)
                throw new InvalidOperationException("Redo did not restore the hover-edited text.");

            int historyBeforeCancel = _historyPosition;
            HandleTextToolClick(hoverScreen);
            _textToolEditField.Text = "Canceled edit";
            HandleTextToolEditorInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (billboardPanel.State.CommentText != editedText || _historyPosition != historyBeforeCancel)
                throw new InvalidOperationException("Cancel did not restore an existing panel without adding history.");

            _textToolStylePicker.Select(1);
            SetTextToolStyle(1);
            RotateTextToolPanel();
            if (_textToolManualQuarterTurns != 1)
                throw new InvalidOperationException("The Text tool rotation action did not advance by one quarter-turn.");
            _textToolSnapToggle.SetPressedNoSignal(true);
            _textToolSnap = true;
            Vector2 fixedPanelScreen = _camera.UnprojectPosition(new Vector3(-7f, 0f, 0f));
            UpdateTextToolPreview(fixedPanelScreen);
            Quaternion fixedPanelRotation = _textToolPlacementRotation;
            Vector3 snappedSurface = _textToolPlacementPosition - Vector3.Up * TextToolPanelHeight;
            if (!_textToolHasPlacement
                || !Mathf.IsEqualApprox(snappedSurface.X / (LegacyTerrainData.CellWidth * 0.5f), Mathf.Round(snappedSurface.X / (LegacyTerrainData.CellWidth * 0.5f)))
                || !Mathf.IsEqualApprox(snappedSurface.Y / (LegacyTerrainData.CellHeight * 0.5f), Mathf.Round(snappedSurface.Y / (LegacyTerrainData.CellHeight * 0.5f)))
                || !Mathf.IsEqualApprox(snappedSurface.Z / (LegacyTerrainData.CellWidth * 0.5f), Mathf.Round(snappedSurface.Z / (LegacyTerrainData.CellWidth * 0.5f))))
                throw new InvalidOperationException("Text panel snapping did not use the legacy terrain grid units.");
            HandleTextToolClick(fixedPanelScreen);
            WorldActorNode fixedPanel = _textToolEditingActor
                ?? throw new InvalidOperationException("The fixed Panel style did not create a CommentPanel.");
            if (fixedPanel.IsBillboardTextPanel || fixedPanel.RenderableUri != "builtin:BuiltinAssets/CommentPanel")
                throw new InvalidOperationException("The Panel style created the wrong legacy renderable.");
            Basis expectedPanelBasis = new(fixedPanelRotation);
            if (!fixedPanel.GlobalBasis.X.Normalized().IsEqualApprox(expectedPanelBasis.X.Normalized())
                || !fixedPanel.GlobalBasis.Y.Normalized().IsEqualApprox(expectedPanelBasis.Y.Normalized())
                || !fixedPanel.GlobalBasis.Z.Normalized().IsEqualApprox(expectedPanelBasis.Z.Normalized()))
                throw new InvalidOperationException("The fixed text panel did not preserve the selected quarter-turn.");
            _textToolEditField.Text = "Fixed panel";
            FinishTextToolEditing();
            if (fixedPanel.State.CommentText != "Fixed panel")
                throw new InvalidOperationException("The fixed Panel style did not save its text.");

            int actorCountBeforeCancel = _actors.Count;
            int historyBeforeCreateCancel = _historyPosition;
            Vector2 cancelScreen = _camera.UnprojectPosition(new Vector3(7f, 0f, 0f));
            UpdateTextToolPreview(cancelScreen);
            if (_textToolHoverActor is not null)
                throw new InvalidOperationException("The canceled-creation fixture overlaps an existing text panel.");
            HandleTextToolClick(cancelScreen);
            if (_textToolEditingActor is null)
                throw new InvalidOperationException("The canceled-creation fixture did not open its editor.");
            CancelTextToolEditing();
            if (_actors.Count != actorCountBeforeCancel || _historyPosition != historyBeforeCreateCancel)
                throw new InvalidOperationException("Cancel left a newly placed panel in the world or added an undo entry.");
            int actorCountBeforeEmptyCommit = _actors.Count;
            int historyBeforeEmptyCommit = _historyPosition;
            UpdateTextToolPreview(cancelScreen);
            HandleTextToolClick(cancelScreen);
            if (_textToolEditingActor is null)
                throw new InvalidOperationException("The empty-panel fixture did not open its editor.");
            FinishTextToolEditing();
            if (_actors.Count != actorCountBeforeEmptyCommit || _historyPosition != historyBeforeEmptyCommit)
                throw new InvalidOperationException("Finishing with the unmodified placeholder should discard the empty panel.");

            _UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
            if (_textToolActive || _textToolPreview?.Visible == true || _textToolToggle.ButtonPressed)
                throw new InvalidOperationException("Escaping the Text tool did not restore normal editor interaction.");
            GD.Print($"TEXT_TOOL_SMOKE_OK create=true liveEdit=true hoverEdit=true cancel=true emptyPlaceholder=true undo=true redo=true billboard=true fixedPanel=true rotation=true snap=true hoverScreenshot={hoverScreenshotPath} editorScreenshot={screenshotPath}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"TEXT_TOOL_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }
}
