using Godot;
using GBNG.World;

namespace GBNG.App;

public partial class GameBuilderApp
{
    private async void RunCommentPanelSmoke()
    {
        int exitCode = 0;
        try
        {
            await WaitForUiFrames(2);
            if (_document is null || _welcome.Visible || !_editor.Visible)
                throw new InvalidOperationException("The comment-panel smoke requires an open project in the editor.");

            if (!AddActor("builtin:BuiltinAssets/CommentPanel"))
                throw new InvalidOperationException("Could not create the legacy CommentPanel fixture.");
            WorldActorNode commentPanel = _selectedActor
                ?? throw new InvalidOperationException("The new CommentPanel was not selected.");
            if (!commentPanel.Scale.IsEqualApprox(Vector3.One * 6f)
                || commentPanel.State.IsSolid
                || commentPanel.State.EnablePhysics
                || commentPanel.State.EnableGravity)
                throw new InvalidOperationException("New text panels did not match the legacy scale and non-physical defaults.");
            PrepareCommentPanelSmokeActor(commentPanel, Vector3.Zero);
            const string originalText = "Text panels\n<size=130%><color=yellow>GOLD</color></size>\nPress [E].";
            SetActorStringProperty(commentPanel.LegacyName, "commentText", originalText);
            if (!commentPanel.SupportsCommentText
                || !_commentTextLabel.Visible
                || !_commentTextField.Visible
                || _commentTextField.Text != originalText)
                throw new InvalidOperationException("The comment-text inspector did not expose and load the selected panel text.");

            LegacyCommentPanelRenderer commentRenderer = commentPanel.CommentPanelRenderer
                ?? throw new InvalidOperationException("The CommentPanel has no Godot text renderer.");
            await WaitForUiFrames(3);
            Image originalImage = commentRenderer.TextViewport?.GetTexture().GetImage()
                ?? throw new InvalidOperationException("The CommentPanel text viewport did not render an image.");
            if (CountVisibleTextPixels(originalImage) < 100 || CountTextPixels(originalImage, color =>
                    color.R > 0.65f && color.G > 0.5f && color.B < 0.3f) < 10)
                throw new InvalidOperationException("Comment text or its legacy yellow rich-text color did not render visibly.");
            string textScreenshot = ProjectSettings.GlobalizePath("res://.godot/comment-panel-text-smoke.png");
            Directory.CreateDirectory(Path.GetDirectoryName(textScreenshot)!);
            if (originalImage.SavePng(textScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the CommentPanel text screenshot.");

            const string editedText = "Live edit\n<size=70%><color=cyan>CYAN</color></size>\nWorks.";
            BeginTextEdit("commentText");
            _commentTextField.Text = editedText;
            UpdateSelectedCommentText();
            EndTextEdit();
            if (commentPanel.State.CommentText != editedText)
                throw new InvalidOperationException("Editing the inspector did not update serialized commentText.");
            UndoEdit();
            if (commentPanel.State.CommentText != originalText)
                throw new InvalidOperationException("Undo did not restore the previous comment-panel text.");
            RedoEdit();
            if (commentPanel.State.CommentText != editedText)
                throw new InvalidOperationException("Redo did not restore the edited comment-panel text.");
            await WaitForUiFrames(3);
            Image editedImage = commentRenderer.TextViewport!.GetTexture().GetImage();
            if (CountVisibleTextPixels(editedImage) < 100 || CountTextPixels(editedImage, color =>
                    color.R < 0.3f && color.G > 0.6f && color.B > 0.6f) < 10)
                throw new InvalidOperationException("Live comment editing did not refresh the rendered cyan text.");
            if (editedImage.SavePng(textScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the edited CommentPanel text screenshot.");

            if (!AddActor("builtin:BuiltinAssets/BillboardTextPanel"))
                throw new InvalidOperationException("Could not create the legacy BillboardTextPanel fixture.");
            WorldActorNode billboardPanel = _selectedActor
                ?? throw new InvalidOperationException("The new BillboardTextPanel was not selected.");
            PrepareCommentPanelSmokeActor(billboardPanel, Vector3.Right * 10f);
            billboardPanel.SetCommentText("Billboard text");
            await WaitForUiFrames(3);
            Vector3 expectedBillboardRotation = new(
                -_camera.GlobalRotation.X,
                Mathf.Pi + _camera.GlobalRotation.Y,
                0f);
            Basis expectedBillboardBasis = new(Quaternion.FromEuler(expectedBillboardRotation));
            Node3D? billboardRenderable = billboardPanel.RenderableRoot;
            if (!billboardPanel.IsBillboardTextPanel || billboardRenderable is null)
                throw new InvalidOperationException("The BillboardTextPanel did not initialize its camera-facing renderer.");
            if (!billboardRenderable.GlobalBasis.X.Normalized().IsEqualApprox(expectedBillboardBasis.X)
                || !billboardRenderable.GlobalBasis.Y.Normalized().IsEqualApprox(expectedBillboardBasis.Y)
                || !billboardRenderable.GlobalBasis.Z.Normalized().IsEqualApprox(expectedBillboardBasis.Z))
                throw new InvalidOperationException($"The BillboardTextPanel did not face the active camera (expected={expectedBillboardBasis}, actual={billboardRenderable.GlobalBasis}).");

            string uiScreenshot = ProjectSettings.GlobalizePath("res://.godot/comment-panels-ui-smoke.png");
            if (GetViewport().GetTexture().GetImage().SavePng(uiScreenshot) != Error.Ok)
                throw new InvalidOperationException("Could not save the comment-panels editor screenshot.");
            GD.Print($"COMMENT_PANELS_SMOKE_OK commentText=rendered richText=color+size inspector=live-edit undo=true redo=true billboard=camera-facing=true textScreenshot={textScreenshot} uiScreenshot={uiScreenshot}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"COMMENT_PANELS_SMOKE_FAILED: {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }

    private void PrepareCommentPanelSmokeActor(WorldActorNode actor, Vector3 cameraOffset)
    {
        actor.State.WriteBool("isSolid", false);
        actor.ApplyLegacyPhysicsSettings();
        actor.GlobalPosition = _camera.GlobalPosition - _camera.GlobalBasis.Z * 12f + cameraOffset;
        actor.GlobalRotation = new Vector3(
            -_camera.GlobalRotation.X,
            Mathf.Pi + _camera.GlobalRotation.Y,
            0f);
    }

    private static int CountVisibleTextPixels(Image image)
        => CountTextPixels(image, _ => true);

    private static int CountTextPixels(Image image, Func<Color, bool> predicate)
    {
        int count = 0;
        Vector2I size = image.GetSize();
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.A > 0.1f && predicate(pixel))
                    count++;
            }
        }
        return count;
    }
}
