// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The Settings screen's frame, recovered from the settings bundle
/// (NPXS40008, <c>rnps-settings</c>) module 24's <c>StyleValues</c>.
///
/// The list is absolutely placed, not flowed: 304 in from each side and 186
/// down, which leaves it running to the bottom of the canvas.
///
/// <b>Row height is deliberately absent.</b> Settings rows are the native
/// <c>MenuListItemPS</c> / <c>ContentListItemPS</c> widgets; the JavaScript
/// supplies the list frame, the per-item margins and whatever is bolted on
/// beside the row, and nothing about the row's own height or internal padding.
/// The one list that does declare a height is the saved-data family at 152, and
/// that number belongs to that family alone - see
/// <see cref="ShellSettingsMetrics.SavedDataRowHeight"/>. Anything here that
/// looks like a row height would be invented.
/// </summary>
public static class ShellSettingsMetrics
{
    // ---- Shared focus renderer -------------------------------------------

    /// <summary>
    /// Settings keeps the capture-approved thin 1.5 px LineFocus band. This
    /// scales the band and its exterior plane; it does not collapse LineFocus
    /// onto AreaFocus's exact row rectangle.
    /// </summary>
    public const double FocusLineScale = 0.5;

    // ---- The default list frame -------------------------------------------

    /// <summary><c>DEFAULT_LISTVIEW_TOP</c>.</summary>
    public const double ListTop = 186.0;

    /// <summary><c>DEFAULT_LISTVIEW_LEFT</c>.</summary>
    public const double ListLeft = 304.0;

    /// <summary><c>DEFAULT_LISTVIEW_WIDTH</c>.</summary>
    public const double ListWidth = 1312.0;

    /// <summary><c>DEFAULT_LISTVIEW_HEIGHT</c>.</summary>
    public const double ListHeight = 894.0;

    /// <summary>
    /// The inset on the right, which falls out of the others rather than being
    /// declared: <c>1920 - 304 - 1312</c>. It matches the left, so the list is
    /// centred even though the bundle never says so.
    /// </summary>
    public const double ListRight = ShellDialog.DesignWidth - ListLeft - ListWidth;

    /// <summary><c>DEFAULT_LISTVIEW_BOTTOM_MARGIN_UNDER_BOTTOM_ITEM</c>.</summary>
    public const double BottomMarginUnderBottomItem = 90.0;

    /// <summary><c>DEFAULT_LISTITEM_MARGIN</c>: rows sit flush by default.</summary>
    public const double ItemMargin = 0.0;

    /// <summary><c>SECTION_TAIL_ITEM_MARGIN</c>: the gap after a section.</summary>
    public const double SectionTailItemMargin = 96.0;

    /// <summary><c>SECTION_ITEM_MARGIN</c>: the gap between rows within one.</summary>
    public const double SectionItemMargin = 14.0;

    /// <summary><c>DEFAULT_INDENT_WIDTH</c>: a sub-item's left padding.</summary>
    public const double IndentWidth = 64.0;

    /// <summary><c>TOP_LABEL_MARGIN_BOTTOM</c>.</summary>
    public const double TopLabelMarginBottom = 36.0;

    /// <summary>
    /// <c>FOCUS_MARGIN</c>. Declared in the bundle and consumed by nothing in
    /// it, so the native list applies it. Carried for completeness.
    /// </summary>
    public const double FocusMargin = 3.0;

    // ---- Row furniture the JavaScript does own ----------------------------

    /// <summary><c>indicatorImage.minWidth</c>: the left indicator gutter.</summary>
    public const double IndicatorMinWidth = 64.0;

    /// <summary><c>additionalButton.minWidth</c>: the trailing button column.</summary>
    public const double AdditionalButtonMinWidth = 104.0;

    /// <summary><c>additionalButton.paddingHorizontal</c>.</summary>
    public const double AdditionalButtonPaddingHorizontal = 16.0;

    /// <summary>
    /// <c>separator.height</c>: a hairline under a row, suppressed on the last
    /// row of a section.
    /// </summary>
    public const double SeparatorHeight = 2.0;

    // ---- LongTextListItem (module 190) -----------------------------------

    /// <summary><c>title.marginLeft</c>.</summary>
    public const double LongTextTitleMarginLeft = 16.0;

    /// <summary><c>title.marginRight</c>.</summary>
    public const double LongTextTitleMarginRight = 48.0;

    /// <summary><c>title.marginTop</c> and <c>title.marginBottom</c>.</summary>
    public const double LongTextTitleMarginVertical = 27.0;

    /// <summary><c>value.marginRight</c>.</summary>
    public const double LongTextValueMarginRight = 16.0;

    /// <summary><c>value.opacity</c>.</summary>
    public const double LongTextValueOpacity = 0.7;

    // ---- Tabbed screens ---------------------------------------------------

    /// <summary><c>DEFAULT_TAB_TOP</c>.</summary>
    public const double TabTop = 186.0;

    /// <summary><c>DEFAULT_HORIZONTAL_TAB_TOP</c>.</summary>
    public const double HorizontalTabTop = 274.0;

    /// <summary><c>DEFAULT_TAB_LEFT</c>.</summary>
    public const double TabLeft = 172.0;

    /// <summary><c>DEFAULT_TAB_WIDTH</c>: the tab column.</summary>
    public const double TabWidth = 388.0;

    /// <summary><c>DEFAULT_TAB_PANEL_LEFT</c>.</summary>
    public const double TabPanelLeft = 96.0;

    /// <summary><c>DEFAULT_TAB_PANEL_WIDTH</c>.</summary>
    public const double TabPanelWidth = 1092.0;

    /// <summary><c>DEFAULT_TAB_PANEL_HEIGHT</c>.</summary>
    public const double TabPanelHeight = 894.0;

    // ---- Popup value selectors ------------------------------------------

    /// <summary><c>popupScroll.maxHeight</c> (SET module 257).</summary>
    public const double PopupMaximumHeight = 504.0;

    /// <summary><c>popupScroll.marginBottom</c> (SET module 257).</summary>
    public const double PopupBottomMargin = 48.0;

    // ---- Select mode ------------------------------------------------------

    /// <summary><c>SELECTMODE_LISTVIEW_LEFT</c>: the list slides left to make
    /// room for the button column.</summary>
    public const double SelectModeListLeft = 172.0;

    /// <summary><c>SELECTMODE_LISTVIEW_WIDTH</c>.</summary>
    public const double SelectModeListWidth = 1092.0;

    /// <summary><c>SELECTMODE_BUTTON_WIDTH</c>. The same 388 as the tab column,
    /// and as the shell's dialog buttons.</summary>
    public const double SelectModeButtonWidth = 388.0;

    /// <summary><c>SELECTMODE_BUTTON_HEIGHT</c>.</summary>
    public const double SelectModeButtonHeight = 72.0;

    /// <summary><c>SELECTMODE_BUTTON_DESELECT_ALL_TOP</c>.</summary>
    public const double SelectModeDeselectAllTop = 96.0;

    /// <summary>
    /// The select-mode slide, in milliseconds. The bundle animates left and
    /// width on a timing curve rather than a spring, unlike the home screen.
    /// </summary>
    public const double SelectModeSlideMs = 250.0;

    /// <summary>The button column's fade, in milliseconds.</summary>
    public const double SelectModeButtonFadeMs = 200.0;

    // ---- The one family that declares a row height ------------------------

    /// <summary>
    /// <c>DEFAULT_LISTITEM_HEIGHT</c> from the saved-data list's own style
    /// values. It is the row pitch for that family only - the saved-data,
    /// game-title and common lists - and generalising it to the main settings
    /// menu would be inventing a number the console does not use there.
    /// </summary>
    public const double SavedDataRowHeight = 152.0;

    /// <summary>The saved-data family's own indent, wider than the default 64.</summary>
    public const double SavedDataIndentWidth = 94.0;

    /// <summary>The saved-data family's checkbox box.</summary>
    public const double SavedDataCheckBoxSize = 40.0;

    // ---- Dialogs ----------------------------------------------------------

    /// <summary><c>MODAL_DIALOG_STYLE.width</c>.</summary>
    public const double ModalDialogWidth = 928.0;

    /// <summary>The settings list's own rect on the design canvas.</summary>
    public static Avalonia.Rect ListBounds => new(ListLeft, ListTop, ListWidth, ListHeight);

    /// <summary>True when the frame is centred, which it is: both insets are 304.</summary>
    public static bool IsCentred => ListLeft == ListRight;
}
