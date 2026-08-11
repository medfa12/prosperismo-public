// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Prosperismo.GUI.SystemAssets.Shell;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Shell-owned SystemModalDialog host. NPXS40087 registers dialogs below the
/// SystemOverlay container, reference-counts them, locks PS/capture input while
/// one is open, and asks BGLayer for a flat basemat instead of drawing a modal
/// scrim. This class owns those responsibilities; <see cref="ShellDialog"/>
/// remains the DialogPS content surface.
/// </summary>
public sealed class ShellModalManager
{
    private readonly Panel _host;
    private readonly IReadOnlyList<ShellBackground> _backgrounds;
    private readonly Dictionary<ShellBackground, ShellBasematType> _savedBasemats = new();
    private readonly List<ShellDialog> _dialogs = new();
    private int _dialogCount;

    public ShellModalManager(Panel host, params ShellBackground[] backgrounds)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _backgrounds = backgrounds ?? throw new ArgumentNullException(nameof(backgrounds));
    }

    public event EventHandler? InputLockChanged;

    public int DialogCount => _dialogCount;
    public bool IsInputLocked => _dialogCount > 0;
    public bool IsPsButtonLocked => IsInputLocked;
    public bool IsCaptureMenuLocked => IsInputLocked;
    public ShellDialog? ActiveDialog => _dialogs.Count == 0 ? null : _dialogs[^1];

    public async Task<string> ShowAsync(ShellDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    completion.TrySetResult(await ShowAsync(request).ConfigureAwait(true));
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return await completion.Task.ConfigureAwait(false);
        }

        ShellDialog? created = null;
        EnterModal();
        try
        {
            return await ShellDialog.ShowAsync(
                _host,
                request,
                usesBackgroundBasemat: true,
                onCreated: dialog =>
                {
                    created = dialog;
                    _dialogs.Add(dialog);
                }).ConfigureAwait(true);
        }
        finally
        {
            if (created is not null)
            {
                _dialogs.Remove(created);
            }
            LeaveModal();
        }
    }

    private void EnterModal()
    {
        _dialogCount++;
        if (_dialogCount != 1)
        {
            return;
        }

        _savedBasemats.Clear();
        _host.IsHitTestVisible = true;
        foreach (var background in _backgrounds)
        {
            _savedBasemats[background] = background.BasematType;
            background.SetBasemat(new ShellLayerBasematRequest(
                ShellBasematType.Flat,
                ShellBackgroundComposition.BasematColor,
                TimeSpan.FromMilliseconds(ShellBackgroundComposition.BasematDurationMilliseconds)));
        }
        InputLockChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LeaveModal()
    {
        if (_dialogCount <= 0)
        {
            return;
        }

        _dialogCount--;
        if (_dialogCount != 0)
        {
            return;
        }

        foreach (var background in _backgrounds)
        {
            var type = _savedBasemats.TryGetValue(background, out var saved)
                ? saved
                : ShellBasematType.None;
            background.SetBasemat(new ShellLayerBasematRequest(
                type,
                ShellBackgroundComposition.BasematColor,
                TimeSpan.FromMilliseconds(ShellBackgroundComposition.BasematDurationMilliseconds)));
        }
        _savedBasemats.Clear();
        _host.IsHitTestVisible = false;
        InputLockChanged?.Invoke(this, EventArgs.Empty);
    }
}
