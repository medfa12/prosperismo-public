// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Prosperismo.GUI.SystemAssets;

/// <summary>Small authored chrome textures provided by the PS5 UI3 resource package.</summary>
public enum Ps5Ui3ChromeAsset
{
    ButtonBase,
    EmphasisButtonBase,
    MenuBase,
    PopupDialogBase,
}

/// <summary>
/// Reads the small, tint-neutral chrome textures from the user's PS5 12.40
/// <c>Sce.PlayStation.PUI_UI3.rco</c>, then falls back to the named packaged
/// PNG derivatives for standalone builds. A missing or malformed source keeps
/// the caller's local fallback presentation.
///
/// The 12.40 package supplies <c>image_button_base</c> (150 x 148 at 4K),
/// <c>image_emphasisbutton_base</c> (22 x 22), <c>image_menu_base</c> (18 x
/// 18), and <c>image_popup_dialog_base</c> (18 x 18). They are source
/// textures for the shell's scale-nine-patch path, not whole button
/// screenshots.
/// </summary>
public static class Ps5Ui3Chrome
{
    private static readonly IReadOnlyDictionary<Ps5Ui3ChromeAsset, string> Names =
        new Dictionary<Ps5Ui3ChromeAsset, string>
        {
            [Ps5Ui3ChromeAsset.ButtonBase] = "image_button_base",
            [Ps5Ui3ChromeAsset.EmphasisButtonBase] = "image_emphasisbutton_base",
            [Ps5Ui3ChromeAsset.MenuBase] = "image_menu_base",
            [Ps5Ui3ChromeAsset.PopupDialogBase] = "image_popup_dialog_base",
        };

    public static Bitmap? TryGet(Ps5Ui3ChromeAsset asset) =>
        Ps5Ui3PackagedTextures.TryGet(PackagedFileName(asset));

    /// <summary>The exact UI3 entry name used for <paramref name="asset"/>.</summary>
    public static string EntryName(Ps5Ui3ChromeAsset asset) => Names[asset];

    private static string PackagedFileName(Ps5Ui3ChromeAsset asset) => asset switch
    {
        Ps5Ui3ChromeAsset.ButtonBase => "button-base.png",
        Ps5Ui3ChromeAsset.EmphasisButtonBase => "emphasis-button-base.png",
        Ps5Ui3ChromeAsset.MenuBase => "menu-base.png",
        Ps5Ui3ChromeAsset.PopupDialogBase => "popup-dialog-base.png",
        _ => throw new ArgumentOutOfRangeException(nameof(asset)),
    };

}

/// <summary>
/// Paints a UI3 chrome texture with nine-patch semantics. It preserves the
/// authored corner pixels while only the opaque centre stretches, so a 72px
/// button can grow horizontally without turning its rounded ends into an
/// ellipse. <see cref="FallbackBrush"/> is always painted first.
/// </summary>
public sealed class Ps5Ui3ChromePlate : Decorator
{
    public Ps5Ui3ChromeAsset Asset { get; set; } = Ps5Ui3ChromeAsset.ButtonBase;

    public IBrush FallbackBrush { get; set; } = Brushes.Transparent;

    /// <summary>Destination corner radius; it does not affect layout.</summary>
    public double SliceCornerRadius { get; set; }

    /// <summary>Opacity applied to the neutral, tintable source texture.</summary>
    public double AssetOpacity { get; set; } = 1.0;

    public Ps5Ui3ChromePlate()
    {
        // The decorator itself stays hit-testable over its full layout box;
        // the RCO alpha mask must not shrink a CTA's input target.
        IsHitTestVisible = true;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            base.Render(context);
            return;
        }

        context.DrawRectangle(FallbackBrush, null, bounds, SliceCornerRadius, SliceCornerRadius);

        if (Ps5Ui3Chrome.TryGet(Asset) is { } bitmap && AssetOpacity > 0)
        {
            using (context.PushOpacity(Math.Clamp(AssetOpacity, 0, 1)))
            {
                DrawNinePatch(context, bitmap, bounds, SliceCornerRadius);
            }
        }

        base.Render(context);
    }

    /// <summary>
    /// Draws all nine source regions. The old centre-plus-corners shortcut
    /// discarded the authored top, bottom, left, and right strips; that is
    /// especially visible on the 18px UI3 menu and dialog plates as a blurred
    /// or broken edge at non-native sizes.
    /// </summary>
    internal static void DrawNinePatch(DrawingContext context, Bitmap bitmap, Rect target, double radius)
    {
        var source = new Rect(bitmap.Size);
        if (source.Width <= 1 || source.Height <= 1)
        {
            context.DrawImage(bitmap, source, target);
            return;
        }

        // UI3's source plates carry a scalable centre bounded by quarters:
        // 18px menu/dialog textures have a 9px middle band, while the
        // 150x148 button source retains its 37px rounded-end caps. Splitting
        // at half would leave a zero-sized centre on square sources and turn
        // their edge strips into gaps.
        var sourceCap = Math.Min(source.Width, source.Height) / 4;
        var targetCap = Math.Min(Math.Max(0, radius), Math.Min(target.Width, target.Height) / 2);
        if (sourceCap <= 0 || targetCap <= 0)
        {
            context.DrawImage(bitmap, source, target);
            return;
        }

        var sourceRightEdge = source.Right - sourceCap;
        var sourceBottomEdge = source.Bottom - sourceCap;
        var targetRightEdge = target.Right - targetCap;
        var targetBottomEdge = target.Bottom - targetCap;

        var sourceCenter = new Rect(source.X + sourceCap, source.Y + sourceCap,
            Math.Max(0, source.Width - (2 * sourceCap)), Math.Max(0, source.Height - (2 * sourceCap)));
        var targetCenter = new Rect(target.X + targetCap, target.Y + targetCap,
            Math.Max(0, target.Width - (2 * targetCap)), Math.Max(0, target.Height - (2 * targetCap)));
        var sourceTop = new Rect(source.X + sourceCap, source.Y, sourceCenter.Width, sourceCap);
        var sourceBottom = new Rect(source.X + sourceCap, sourceBottomEdge, sourceCenter.Width, sourceCap);
        var sourceLeft = new Rect(source.X, source.Y + sourceCap, sourceCap, sourceCenter.Height);
        var sourceRight = new Rect(sourceRightEdge, source.Y + sourceCap, sourceCap, sourceCenter.Height);
        var targetTop = new Rect(target.X + targetCap, target.Y, targetCenter.Width, targetCap);
        var targetBottom = new Rect(target.X + targetCap, targetBottomEdge, targetCenter.Width, targetCap);
        var targetLeft = new Rect(target.X, target.Y + targetCap, targetCap, targetCenter.Height);
        var targetRight = new Rect(targetRightEdge, target.Y + targetCap, targetCap, targetCenter.Height);

        context.DrawImage(bitmap, new Rect(source.X, source.Y, sourceCap, sourceCap),
            new Rect(target.X, target.Y, targetCap, targetCap));
        context.DrawImage(bitmap, sourceTop, targetTop);
        context.DrawImage(bitmap, new Rect(sourceRightEdge, source.Y, sourceCap, sourceCap),
            new Rect(targetRightEdge, target.Y, targetCap, targetCap));
        context.DrawImage(bitmap, sourceLeft, targetLeft);
        context.DrawImage(bitmap, sourceCenter, targetCenter);
        context.DrawImage(bitmap, sourceRight, targetRight);
        context.DrawImage(bitmap, new Rect(source.X, sourceBottomEdge, sourceCap, sourceCap),
            new Rect(target.X, targetBottomEdge, targetCap, targetCap));
        context.DrawImage(bitmap, sourceBottom, targetBottom);
        context.DrawImage(bitmap, new Rect(sourceRightEdge, sourceBottomEdge, sourceCap, sourceCap),
            new Rect(targetRightEdge, targetBottomEdge, targetCap, targetCap));
    }
}
