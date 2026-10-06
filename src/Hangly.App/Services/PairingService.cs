using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Firebase.Database;
using Firebase.Database.Query;

namespace Hangly.App.Services;

public class PairingService
{
    // Your exact Firebase Database URL:
    private const string FirebaseUrl = "https://hangly-57171-default-rtdb.firebaseio.com/";

    private readonly FirebaseClient _client;
    private readonly string _clientId = Guid.NewGuid().ToString();
    private IDisposable? _subscription;

    public string? CurrentRoomCode { get; private set; }
    public bool IsHost { get; private set; }

    public event Action<string>? CharmChangedFromPartner;
    public event Action<string>? ConnectionRequested;
    public event Action? ConnectionAccepted;
    public event Action? ConnectionDeclined;

    public PairingService()
    {
        _client = new FirebaseClient(FirebaseUrl);

        // Safe auto-reconnect from local settings file
        string? savedCode = LoadSavedCode();
        if (!string.IsNullOrEmpty(savedCode))
        {
            CurrentRoomCode = savedCode;
            ListenToRoom(savedCode);
        }
    }

    public async Task<string> GenerateRoomCodeAsync(string initialCharmId)
    {
        IsHost = true;
        string code = Random.Shared.Next(1000, 9999).ToString();
        CurrentRoomCode = code;

        SaveCode(code);
        ListenToRoom(code);

        await _client.Child("rooms").Child(code).PutAsync(new RoomState
        {
            HostId = _clientId,
            Status = "waiting",
            CharmId = initialCharmId,
            LastSenderId = _clientId
        });

        return code;
    }

    public async Task RequestJoinRoomAsync(string code)
    {
        IsHost = false;
        CurrentRoomCode = code;
        ListenToRoom(code);

        await _client.Child("rooms").Child(code).PatchAsync(new
        {
            guestId = _clientId,
            status = "requested"
        });
    }

    public async Task RespondToRequestAsync(bool accept)
    {
        if (string.IsNullOrEmpty(CurrentRoomCode)) return;

        string newStatus = accept ? "accepted" : "declined";
        await _client.Child("rooms").Child(CurrentRoomCode).PatchAsync(new
        {
            status = newStatus
        });
    }

    private void ListenToRoom(string code)
    {
        _subscription?.Dispose();

        _subscription = _client
            .Child("rooms")
            .Child(code)
            .AsObservable<RoomState>()
            .Subscribe(d =>
            {
                if (d.Object == null) return;
                var room = d.Object;

                // 1. Host receives request from Guest
                if (IsHost && room.Status == "requested" && room.GuestId != _clientId)
                {
                    ConnectionRequested?.Invoke(room.GuestId);
                }

                // 2. Guest receives Host response
                if (!IsHost && room.Status == "accepted")
                {
                    SaveCode(code);
                    ConnectionAccepted?.Invoke();
                }
                else if (!IsHost && room.Status == "declined")
                {
                    ConnectionDeclined?.Invoke();
                }

                // 3. Charm synchronization
                if (room.Status == "accepted" && room.LastSenderId != _clientId && !string.IsNullOrEmpty(room.CharmId))
                {
                    CharmChangedFromPartner?.Invoke(room.CharmId);
                }
            });
    }

    public async Task SendCharmUpdateAsync(string charmId)
    {
        if (string.IsNullOrEmpty(CurrentRoomCode)) return;

        await _client.Child("rooms").Child(CurrentRoomCode).PatchAsync(new
        {
            charmId = charmId,
            lastSenderId = _clientId
        });
    }

    public void Disconnect()
    {
        RemoveCode();
        CurrentRoomCode = null;
        _subscription?.Dispose();
    }

    // --- Safe Unpackaged Local File Storage ---
    private static string GetStorageFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hangly");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pair_code.txt");
    }

    private static string? LoadSavedCode()
    {
        try
        {
            string path = GetStorageFilePath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch { return null; }
    }

    private static void SaveCode(string code)
    {
        try
        {
            File.WriteAllText(GetStorageFilePath(), code);
        }
        catch { }
    }

    private static void RemoveCode()
    {
        try
        {
            string path = GetStorageFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}

public class RoomState
{
    public string HostId { get; set; } = string.Empty;
    public string GuestId { get; set; } = string.Empty;
    public string Status { get; set; } = "waiting";
    public string CharmId { get; set; } = string.Empty;
    public string LastSenderId { get; set; } = string.Empty;
}