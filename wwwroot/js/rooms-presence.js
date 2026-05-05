(function () {
    "use strict";

    // Rooms page presence — connects to SignalR to get live online counts
    // and unread badge updates without needing a full chat room open.

    var HEARTBEAT_MS = 25000;

    function getUsername() {
        return (window.roomsConfig && window.roomsConfig.username) || "";
    }

    function getTotalOnlineCount() {
        // Sum all unique online users shown in the sidebar member counts.
        // The server broadcasts PresenceUpdated per-room; we accumulate them.
        return _totalOnline;
    }

    var _totalOnline = 0;
    var _onlineByRoom = {};   // room -> count
    var _lastCounts = {};     // unread counts

    var connection = null;
    var heartbeatTimer = null;
    var reconnectTimer = null;

    function buildConnection() {
        return new signalR.HubConnectionBuilder()
            .withUrl("/chatHub", {
                transport: signalR.HttpTransportType.LongPolling
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();
    }

    function updateUnreadBadges(counts) {
        if (!counts) return;
        _lastCounts = counts;
        Object.keys(counts).forEach(function (room) {
            var count = counts[room];
            var badge = document.getElementById("badge-" + room);
            if (!badge) return;
            if (count > 0) {
                badge.textContent = count > 99 ? "99+" : String(count);
                badge.style.display = "inline-block";
            } else {
                badge.style.display = "none";
                badge.textContent = "";
            }
        });
        // Hide any badge that's no longer in the counts object
        document.querySelectorAll(".unread-badge").forEach(function (el) {
            var room = (el.id || "").replace("badge-", "");
            if (room && !counts[room]) {
                el.style.display = "none";
                el.textContent = "";
            }
        });
    }

    function sendHeartbeat() {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
        var u = getUsername();
        if (!u) return;
        // Use "Lobby" as the presence room on the Rooms page
        connection.invoke("Heartbeat", u, "Lobby").catch(function () {});
    }

    async function startConnection() {
        var username = getUsername();
        if (!username) return;

        connection = buildConnection();

        // When any room's presence changes, refresh the total online count
        connection.on("PresenceUpdated", function (data) {
            var room = data.room || data.Room || "";
            var count = (data.onlineCount !== undefined ? data.onlineCount : (data.OnlineCount || 0));
            if (room) {
                _onlineByRoom[room] = count;
                // Recalculate total: count members that are online across all rooms
                // PresenceUpdated includes the full member list with isOnline flags
                var members = data.members || data.Members || [];
                if (members.length > 0) {
                    // Update our per-room online set using full member data
                    var onlineInRoom = members.filter(function(m) {
                        return m.isOnline || m.IsOnline;
                    }).map(function(m) { return (m.username || m.Username || "").toLowerCase(); });
                    _onlineByRoom[room] = onlineInRoom;
                }
                refreshTotalOnline();
            }
        });

        // Live unread badge updates
        connection.on("UnreadCountsUpdated", function (counts) {
            updateUnreadBadges(counts);
        });

        // When rooms list updates (e.g. new room created by another user)
        connection.on("RoomsUpdated", function () {
            // Full page refresh is safest so the server renders the new room list
            window.location.reload();
        });

        connection.onreconnected(function () {
            var u = getUsername();
            connection.invoke("RegisterConnection", u).catch(function () {});
            sendHeartbeat();
        });

        try {
            await connection.start();
            await connection.invoke("RegisterConnection", username);
            // Join Lobby group so we receive PresenceUpdated for the lobby
            // and so the server knows we are online
            await connection.invoke("OpenRoom", "Lobby", username);

            heartbeatTimer = setInterval(sendHeartbeat, HEARTBEAT_MS);
        } catch (e) {
            reconnectTimer = setTimeout(startConnection, 5000);
        }
    }

    window.addEventListener("beforeunload", function () {
        if (heartbeatTimer) clearInterval(heartbeatTimer);
        if (reconnectTimer) clearTimeout(reconnectTimer);
        if (connection) connection.stop();
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", startConnection);
    } else {
        startConnection();
    }

})();
