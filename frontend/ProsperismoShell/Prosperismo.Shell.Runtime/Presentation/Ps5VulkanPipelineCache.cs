// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using Silk.NET.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Device-validated disk cache for the host pipelines translated from the
/// NPXS40087 shader programs. Vulkan owns compatibility validation; stale or
/// corrupt blobs are ignored and replaced without changing shader inputs.
/// </summary>
internal sealed unsafe class Ps5VulkanPipelineCache : IDisposable
{
    private const int MaximumCacheBytes = 64 * 1024 * 1024;
    private const string CacheFormat = "prosperismo-vulkan-pipeline-v1";
    private const string CacheDirectoryEnvironmentVariable =
        "PROSPERISMO_VULKAN_PIPELINE_CACHE_DIR";

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly string _path;
    private PipelineCache _cache;
    private bool _disposed;

    private Ps5VulkanPipelineCache(
        Vk vk,
        Device device,
        PipelineCache cache,
        string path)
    {
        _vk = vk;
        _device = device;
        _cache = cache;
        _path = path;
    }

    internal PipelineCache Handle => _cache;

    internal static Ps5VulkanPipelineCache Create(
        Vk vk,
        Device device,
        string identity,
        params ReadOnlyMemory<byte>[] programs)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(programs);

        var path = Path.Combine(ResolveCacheDirectory(), BuildCacheFileName(identity, programs));
        var initialData = TryReadInitialData(path);
        var result = CreateNativeCache(vk, device, initialData, out var cache);
        if (result != Result.Success && initialData.Length > 0)
        {
            Trace($"native pipeline cache rejected; rebuilding {Path.GetFileName(path)}");
            initialData = [];
            result = CreateNativeCache(vk, device, initialData, out cache);
        }

        if (result != Result.Success)
        {
            throw new InvalidOperationException($"vkCreatePipelineCache failed: {result}");
        }

        Trace(
            initialData.Length > 0
                ? $"native pipeline cache loaded: {initialData.Length:N0} bytes"
                : "native pipeline cache cold");
        return new Ps5VulkanPipelineCache(vk, device, cache, path);
    }

    internal static string BuildCacheFileName(
        string identity,
        IReadOnlyList<ReadOnlyMemory<byte>> programs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(programs);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(CacheFormat));
        Append(hash, Encoding.UTF8.GetBytes(identity));
        foreach (var program in programs)
        {
            Append(hash, program.Span);
        }

        return $"{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}.vkpc";
    }

    internal void Persist()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        nuint byteCount = 0;
        var result = _vk.GetPipelineCacheData(_device, _cache, &byteCount, null);
        if (result != Result.Success || byteCount == 0 || byteCount > MaximumCacheBytes)
        {
            Trace($"native pipeline cache export skipped: {result}, {byteCount:N0} bytes");
            return;
        }

        var data = new byte[(int)byteCount];
        fixed (byte* destination = data)
        {
            result = _vk.GetPipelineCacheData(_device, _cache, &byteCount, destination);
        }
        if (result != Result.Success || byteCount == 0 || byteCount > (nuint)data.Length)
        {
            Trace($"native pipeline cache export failed: {result}");
            return;
        }

        if ((nuint)data.Length != byteCount)
        {
            Array.Resize(ref data, (int)byteCount);
        }

        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
            Trace($"native pipeline cache saved: {data.Length:N0} bytes");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace($"native pipeline cache save skipped: {exception.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_cache.Handle != 0 && _device.Handle != 0)
        {
            _vk.DestroyPipelineCache(_device, _cache, null);
            _cache = default;
        }
    }

    private static Result CreateNativeCache(
        Vk vk,
        Device device,
        ReadOnlySpan<byte> initialData,
        out PipelineCache cache)
    {
        fixed (byte* initialPointer = initialData)
        {
            var createInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)initialData.Length,
                PInitialData = initialData.IsEmpty ? null : initialPointer,
            };
            return vk.CreatePipelineCache(device, in createInfo, null, out cache);
        }
    }

    private static byte[] TryReadInitialData(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumCacheBytes)
            {
                return [];
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace($"native pipeline cache read skipped: {exception.Message}");
            return [];
        }
    }

    private static string ResolveCacheDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(CacheDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Prosperismo",
            "vulkan-pipeline-cache");
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> byteCount = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(byteCount, bytes.Length);
        hash.AppendData(byteCount);
        hash.AppendData(bytes);
    }

    private static void Trace(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("PROSPERISMO_PS5_NATIVE_TRACE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(message);
        }
    }
}
