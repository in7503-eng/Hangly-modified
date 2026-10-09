using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hangly.App.Services;

public class PairingService
{
    private const string FirebaseUrl = "https://hangly-57171-default-rtdb.firebaseio.com/";
    private readonly HttpClient _http = new();

    // Unique ID per running instance so testing two apps on one PC works
    private readonly string _deviceId = Guid.NewGuid().ToString("N")[..8];

    public string? CurrentRoomCode { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsPartnerOnline { get; private set; }

    public string MyDeviceName => Environment.MachineName;
    public string? PartnerDeviceName { get; private set; }

    public event Action<string, string>? PairingRequested; // (guestId, guestDeviceName)
    public event Action? PairingAccepted;
    public event Action? PairingDeclined;
    public event Action? Unpaired;
    public event Action<string>? CharmChangedFromPartner;
    public event Action<string, string>? CustomCharmReceivedFromPartner; // (name, base64)
    public event Action<string>? AutoConnected;
    public event Action<bool>? PartnerPresenceChanged;

    private CancellationTokenSource? _pollCts;
    private string _lastCharm = string.Empty;
    private long _lastHeartbeatSent;
    private long _previousPartnerLastSeen;
    private long _lastTimePartnerSeenUpdated;

    private static string SaveFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangly", "paired_room.txt");

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    public void SavePairedRoom(string roomCode, string role, string partnerDevice)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SaveFilePath)!);
            File.WriteAllText(SaveFilePath, $"{roomCode}|{role}|{partnerDevice}");
        }
        catch { }
    }

    public (string roomCode, string role, string partnerDevice)? LoadSavedPairedRoom()
    {
        try
        {
            if (File.Exists(SaveFilePath))
            {
                var content = File.ReadAllText(SaveFilePath).Trim();
                var parts = content.Split('|');
                if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    string partnerName = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "Partner Device";
                    return (parts[0], parts[1], partnerName);
                }
            }
        }
        catch { }
        return null;
    }

    public void ClearSavedPairedRoom()
    {
        try
        {
            if (File.Exists(SaveFilePath))
            {
                File.Delete(SaveFilePath);
            }
        }
        catch { }
    }

    public void TryAutoConnect()
    {
        var saved = LoadSavedPairedRoom();
        if (saved == null) return;

        var (roomCode, role, partnerDevice) = saved.Value;
        CurrentRoomCode = roomCode;
        IsHost = role == "host";
        PartnerDeviceName = partnerDevice;
        StartPollingLoop(isAutoConnecting: true);
    }

    public async Task<string> GenerateRoomCodeAsync(string? currentCharm = null)
    {
        StopPolling();
        IsHost = true;
        IsConnected = false;
        IsPartnerOnline = false;
        PartnerDeviceName = null;
        _previousPartnerLastSeen = 0;
        _lastTimePartnerSeenUpdated = 0;
        CurrentRoomCode = Random.Shared.Next(1000, 10000).ToString();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var payload = new
        {
            hostId = _deviceId,
            hostDeviceName = MyDeviceName,
            status = "waiting",
            charm = currentCharm ?? "classic",
            createdAt = now,
            hostLastSeen = now
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await _http.PutAsync($"{FirebaseUrl}rooms/{CurrentRoomCode}.json", content);
        res.EnsureSuccessStatusCode();

        StartPollingLoop(isAutoConnecting: false);
        return CurrentRoomCode;
    }

    public async Task<bool> RequestJoinRoomAsync(string roomCode)
    {
        StopPolling();
        IsHost = false;
        IsConnected = false;
        IsPartnerOnline = false;
        PartnerDeviceName = null;
        _previousPartnerLastSeen = 0;
        _lastTimePartnerSeenUpdated = 0;
        CurrentRoomCode = roomCode.Trim();

        var res = await _http.GetAsync($"{FirebaseUrl}rooms/{CurrentRoomCode}.json");
        if (!res.IsSuccessStatusCode) return false;

        var json = await res.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(json) || json == "null") return false;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        string hostDeviceName = root.TryGetProperty("hostDeviceName", out var hdn) ? hdn.GetString() ?? "" : "";

        if (status != "waiting") return false;

        if (!string.IsNullOrEmpty(hostDeviceName))
        {
            PartnerDeviceName = hostDeviceName;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var patch = new
        {
            guestId = _deviceId,
            guestDeviceName = MyDeviceName,
            status = "requested",
            guestLastSeen = now
        };

        var patchContent = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json");
        var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
        {
            Content = patchContent
        };
        var patchRes = await _http.SendAsync(patchReq);
        if (!patchRes.IsSuccessStatusCode) return false;

        StartPollingLoop(isAutoConnecting: false);
        return true;
    }

    public async Task RespondToRequestAsync(bool accept)
    {
        if (string.IsNullOrEmpty(CurrentRoomCode)) return;

        var patch = new { status = accept ? "connected" : "declined" };
        var patchContent = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
        {
            Content = patchContent
        };
        await _http.SendAsync(req);

        if (accept)
        {
            IsConnected = true;
            IsPartnerOnline = true;
            _lastTimePartnerSeenUpdated = Environment.TickCount64;
            SavePairedRoom(CurrentRoomCode, "host", PartnerDeviceName ?? "Partner Device");
        }
    }

    public async Task DisconnectAndUnpairAsync()
    {
        StopPolling();
        ClearSavedPairedRoom();

        if (!string.IsNullOrEmpty(CurrentRoomCode))
        {
            try
            {
                var patch = new { status = "unpaired" };
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
                {
                    Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
                };
                await _http.SendAsync(req);
            }
            catch { }
        }

        CurrentRoomCode = null;
        IsConnected = false;
        IsPartnerOnline = false;
        PartnerDeviceName = null;
        _previousPartnerLastSeen = 0;
        _lastTimePartnerSeenUpdated = 0;
        Unpaired?.Invoke();
    }

    public async Task CancelRoomAsync()
    {
        StopPolling();
        if (!string.IsNullOrEmpty(CurrentRoomCode))
        {
            try
            {
                await _http.DeleteAsync($"{FirebaseUrl}rooms/{CurrentRoomCode}.json");
            }
            catch { }
            CurrentRoomCode = null;
        }
        IsConnected = false;
        IsPartnerOnline = false;
        PartnerDeviceName = null;
        _previousPartnerLastSeen = 0;
        _lastTimePartnerSeenUpdated = 0;
    }

    public async Task CancelRequestAsync()
    {
        StopPolling();
        if (!string.IsNullOrEmpty(CurrentRoomCode))
        {
            try
            {
                var patch = new
                {
                    guestId = "",
                    guestDeviceName = "",
                    status = "waiting"
                };
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
                {
                    Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
                };
                await _http.SendAsync(req);
            }
            catch { }
            CurrentRoomCode = null;
        }
        IsConnected = false;
        IsPartnerOnline = false;
        PartnerDeviceName = null;
        _previousPartnerLastSeen = 0;
        _lastTimePartnerSeenUpdated = 0;
    }

    public async Task SendCharmUpdateAsync(string charmId)
    {
        if (!IsConnected || string.IsNullOrEmpty(CurrentRoomCode)) return;
        _lastCharm = charmId;

        try
        {
            var patch = new { charm = charmId, customBase64 = "", customName = "" };
            var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
            {
                Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
            };
            await _http.SendAsync(req);
        }
        catch { }
    }

    public async Task SendCustomCharmUpdateAsync(string charmId, string charmName, string base64Data)
    {
        if (!IsConnected || string.IsNullOrEmpty(CurrentRoomCode)) return;
        _lastCharm = charmId;

        try
        {
            var patch = new { charm = charmId, customName = charmName, customBase64 = base64Data };
            var req = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
            {
                Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json")
            };
            await _http.SendAsync(req);
        }
        catch { }
    }

    private void StartPollingLoop(bool isAutoConnecting)
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrEmpty(CurrentRoomCode)) break;

                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Send heartbeat every 2 seconds if connected or pairing
                    if (now - _lastHeartbeatSent >= 2)
                    {
                        _lastHeartbeatSent = now;
                        var hbPatch = IsHost ? (object)new { hostLastSeen = now } : (object)new { guestLastSeen = now };
                        var hbReq = new HttpRequestMessage(new HttpMethod("PATCH"), $"{FirebaseUrl}rooms/{CurrentRoomCode}.json")
                        {
                            Content = new StringContent(JsonSerializer.Serialize(hbPatch), Encoding.UTF8, "application/json")
                        };
                        _ = await _http.SendAsync(hbReq, token);
                    }

                    var res = await _http.GetAsync($"{FirebaseUrl}rooms/{CurrentRoomCode}.json", token);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(token);
                        if (!string.IsNullOrEmpty(json) && json != "null")
                        {
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                            string guestId = root.TryGetProperty("guestId", out var g) ? g.GetString() ?? "" : "";
                            string charm = root.TryGetProperty("charm", out var c) ? c.GetString() ?? "" : "";
                            string guestDeviceName = root.TryGetProperty("guestDeviceName", out var gdn) ? gdn.GetString() ?? "" : "";
                            string hostDeviceName = root.TryGetProperty("hostDeviceName", out var hdn) ? hdn.GetString() ?? "" : "";
                            long hostLastSeen = root.TryGetProperty("hostLastSeen", out var hls) ? hls.GetInt64() : 0L;
                            long guestLastSeen = root.TryGetProperty("guestLastSeen", out var gls) ? gls.GetInt64() : 0L;

                            string remotePartnerDevice = IsHost ? guestDeviceName : hostDeviceName;
                            if (!string.IsNullOrEmpty(remotePartnerDevice))
                            {
                                PartnerDeviceName = remotePartnerDevice;
                            }

                            long partnerLastSeen = IsHost ? guestLastSeen : hostLastSeen;

                            // Handle Host incoming request
                            if (IsHost && !IsConnected && status == "requested" && !string.IsNullOrEmpty(guestId) && guestId != _deviceId)
                            {
                                PartnerDeviceName = guestDeviceName;
                                PairingRequested?.Invoke(guestId, guestDeviceName);
                            }
                            // Handle Guest accepted
                            else if (!IsHost && !IsConnected && status == "connected")
                            {
                                IsConnected = true;
                                IsPartnerOnline = true;
                                _lastTimePartnerSeenUpdated = Environment.TickCount64;
                                SavePairedRoom(CurrentRoomCode, "guest", PartnerDeviceName ?? "Partner Device");
                                PairingAccepted?.Invoke();
                            }
                            // Handle Auto-Connect resolution
                            else if (isAutoConnecting && !IsConnected && status == "connected")
                            {
                                IsConnected = true;
                                IsPartnerOnline = true;
                                _lastTimePartnerSeenUpdated = Environment.TickCount64;
                                isAutoConnecting = false;
                                AutoConnected?.Invoke(CurrentRoomCode);
                            }

                            // If connected: sync charm & live presence
                            if (IsConnected && status == "connected")
                            {
                                long currentTick = Environment.TickCount64;

                                // Check if partner heartbeat was updated in Firebase
                                if (partnerLastSeen > 0 && partnerLastSeen != _previousPartnerLastSeen)
                                {
                                    _previousPartnerLastSeen = partnerLastSeen;
                                    _lastTimePartnerSeenUpdated = currentTick;
                                }

                                // Partner is online if we connected and heartbeat updated within last 10 seconds
                                bool partnerOnline = (_lastTimePartnerSeenUpdated > 0 && (currentTick - _lastTimePartnerSeenUpdated) <= 10000);
                                if (partnerOnline != IsPartnerOnline)
                                {
                                    IsPartnerOnline = partnerOnline;
                                    PartnerPresenceChanged?.Invoke(partnerOnline);
                                }

                                // Sync charm (custom image base64 or built-in ID)
                                if (!string.IsNullOrEmpty(charm) && charm != _lastCharm)
                                {
                                    _lastCharm = charm;

                                    string customBase64 = root.TryGetProperty("customBase64", out var cb) ? cb.GetString() ?? "" : "";
                                    string customName = root.TryGetProperty("customName", out var cn) ? cn.GetString() ?? "" : "";

                                    if (!string.IsNullOrEmpty(customBase64))
                                    {
                                        CustomCharmReceivedFromPartner?.Invoke(customName, customBase64);
                                    }
                                    else
                                    {
                                        CharmChangedFromPartner?.Invoke(charm);
                                    }
                                }
                            }
                            else if (status == "declined")
                            {
                                IsConnected = false;
                                IsPartnerOnline = false;
                                ClearSavedPairedRoom();
                                PairingDeclined?.Invoke();
                                StopPolling();
                                break;
                            }
                            else if (status == "unpaired")
                            {
                                IsConnected = false;
                                IsPartnerOnline = false;
                                ClearSavedPairedRoom();
                                StopPolling();
                                CurrentRoomCode = null;
                                PartnerDeviceName = null;
                                Unpaired?.Invoke();
                                break;
                            }
                        }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch { }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch
                {
                    break;
                }
            }
        }, token);
    }
}