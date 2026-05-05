(function () {
    "use strict";

    // How often to send a keepalive heartbeat via SignalR (ms).
    var HEARTBEAT_INTERVAL_MS = 20000;

    function getConfig() {
        var c = window.chatConfig || { username: "Guest", displayName: "Guest", room: "General" };
        // Fallback: if displayName not set, use username
        if (!c.displayName) c.displayName = c.username;
        return c;
    }

    // -------------------------------------------------------------------------
    // SignalR connection
    // -------------------------------------------------------------------------

    var connection = null;
    var heartbeatTimer = null;
    var reconnectTimer = null;
    var isIntentionallyStopped = false;

    function buildConnection() {
        return new signalR.HubConnectionBuilder()
            .withUrl("/chatHub", {
                // WebSockets is blocked by a proxy in this environment.
                // SSE is server→client only so invoke() silently fails.
                // LongPolling is fully bidirectional and works everywhere.
                transport: signalR.HttpTransportType.LongPolling
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();
    }

    // -------------------------------------------------------------------------
    // UI helpers
    // -------------------------------------------------------------------------

    function updateOnlineCount(n) {
        var byId  = document.getElementById("onlineCount");
        var byCls = document.querySelector(".online-count");
        if (byId)  byId.textContent  = n + " online";
        if (byCls) byCls.textContent = n;
    }

    function renderMembers(data) {
        var memberList = document.querySelector(".member-list");
        if (!memberList) return;

        var members = data.members || [];

        // Derive online count directly from the member list — more accurate than
        // data.onlineCount which can be computed before the latest state change
        var onlineNow = members.filter(function (m) { return m.isOnline; }).length;
        updateOnlineCount(onlineNow);

        if (members.length === 0) {
            memberList.innerHTML = '<div class="empty-members-state">No members to display.</div>';
            return;
        }

        // Online users first, then alphabetical within each group
        var sorted = members.slice().sort(function (a, b) {
            if (a.isOnline !== b.isOnline) return a.isOnline ? -1 : 1;
            var nameA = (a.displayName && a.displayName.trim()) ? a.displayName : a.username;
            var nameB = (b.displayName && b.displayName.trim()) ? b.displayName : b.username;
            return nameA.localeCompare(nameB);
        });

        memberList.innerHTML = sorted.map(function (m) {
            // Show displayName if available, fall back to username
            var name       = (m.displayName && m.displayName.trim()) ? m.displayName : m.username;
            var initial    = name ? name.charAt(0).toUpperCase() : "?";
            var statusText = m.isOnline ? "Online" : "Offline";
            var cardCls    = "member-card" + (m.isOnline ? "" : " member-offline");
            var statusCls  = "member-status " + (m.isOnline ? "member-status-online" : "member-status-offline");
            return '<div class="' + cardCls + '">' +
                '<div class="member-left">' +
                '<div class="member-avatar">' + escapeHtml(initial) + "</div>" +
                '<div class="member-name">' + escapeHtml(name) + "</div>" +
                "</div>" +
                '<div class="' + statusCls + '">' + statusText + "</div>" +
                "</div>";
        }).join("");

        // Keep member count badge in header in sync
        var countBadge = document.getElementById("memberCountBadge");
        if (countBadge) countBadge.textContent = "(" + onlineNow + "/" + members.length + ")";

        // Reveal member list now that display names are populated
        if (memberList) memberList.style.visibility = "visible";
    }

    function buildMessageHtml(sender, text, timeStr, isMine, replyToUsername, replyToText, displaySender) {
        var replyBlock = "";
        if (replyToUsername) {
            replyBlock = '<div class="reply-quote">' +
                '<span class="reply-quote-user">' + escapeHtml(replyToUsername) + '</span>' +
                '<span class="reply-quote-text">' + escapeHtml((replyToText || "").substring(0, 120)) + '</span>' +
                '</div>';
        }
        var shownSender = displaySender || sender;
        var initial = shownSender ? shownSender.charAt(0).toUpperCase() : "?";
        return '<div class="message-row ' + (isMine ? "mine" : "theirs") + '">' +
            '<div class="message-avatar">' + escapeHtml(initial) + "</div>" +
            '<div class="message-block">' +
            '<div class="message-meta">' +
            '<span class="message-user">' + escapeHtml(shownSender) + "</span>" +
            '<span class="message-time">' + timeStr + "</span>" +
            '<button type="button" class="reply-btn" ' +
                'data-sender="' + escapeHtml(sender) + '" ' +
                'data-text="' + escapeHtml(text) + '" title="Reply">&#x21A9;</button>' +
            "</div>" +
            replyBlock +
            '<div class="message-bubble ' + (isMine ? "mine" : "other") + '">' + escapeHtml(text) + "</div>" +
            "</div>" +
            "</div>";
    }

    function appendMessage(msg) {
        var panel = document.getElementById("chatMessagePanel");
        if (!panel) return;

        var cfg = getConfig();
        var sender          = msg.username || msg.Username || "";
        var text            = msg.message  || msg.Message  || "";
        var tsRaw           = msg.timestampUtc || msg.TimestampUtc || new Date().toISOString();
        var replyToUsername = msg.replyToUsername || msg.ReplyToUsername || "";
        var replyToText     = msg.replyToText     || msg.ReplyToText     || "";

        var isMine        = sender.toLowerCase() === cfg.username.toLowerCase() ||
                            sender.toLowerCase() === cfg.displayName.toLowerCase();
        var displaySender = isMine ? cfg.displayName : sender;
        var ts            = new Date(tsRaw);
        var timeStr       = pad(ts.getHours()) + ":" + pad(ts.getMinutes()) + ":" + pad(ts.getSeconds());

        var empty = panel.querySelector(".empty-chat-state");
        if (empty) empty.remove();

        panel.insertAdjacentHTML("beforeend",
            buildMessageHtml(sender, text, timeStr, isMine, replyToUsername, replyToText, displaySender));
        panel.scrollTop = panel.scrollHeight;
    }

    function updateRabbitStatus(isConnected) {
        var el = document.querySelector(".rabbit-status");
        if (el) el.textContent = isConnected ? "Connected" : "Disconnected";
    }

    var _lastUnreadCounts = {};
    function updateUnreadBadges(counts) {
        var cfg = getConfig();
        if (counts) {
            // Always zero out the current room in the incoming counts
            // so opening a room immediately clears its badge
            _lastUnreadCounts = Object.assign({}, counts);
            _lastUnreadCounts[cfg.room] = 0;
        }
        document.querySelectorAll(".unread-badge").forEach(function (el) {
            el.style.display = "none";
            el.textContent = "";
        });
        Object.keys(_lastUnreadCounts).forEach(function (room) {
            if (room.toLowerCase() === cfg.room.toLowerCase()) return;
            var count = _lastUnreadCounts[room];
            if (count > 0) {
                var badge = document.getElementById("badge-" + room);
                if (badge) {
                    badge.textContent = count > 99 ? "99+" : String(count);
                    badge.style.display = "inline-block";
                }
            }
        });
    }

    function updateRoomList(rooms) {
        var roomList = document.getElementById("roomList");
        if (!roomList || !rooms) return;

        var cfg = getConfig();
        roomList.innerHTML = rooms.map(function (r) {
            var isActive = r.name.toLowerCase() === cfg.room.toLowerCase();
            var n = r.name.toLowerCase();
            var icon = (n === "lobby" || n === "general") ? "\uD83C\uDFE0" : "#";
            var tag = r.isPrivate ? '<span class="room-tag">Private</span>' : "";
            return '<form method="post" action="/Chat?handler=JoinRoom" class="room-form">' +
                '<input type="hidden" name="roomName" value="' + escapeHtml(r.name) + '" />' +
                '<button type="submit" class="room-button' + (isActive ? " active" : "") + '">' +
                '<span class="room-left">' +
                '<span class="room-hash">' + icon + '</span>' +
                '<span class="room-name">' + escapeHtml(r.name) + "</span>" +
                "</span>" +
                '<span class="room-right">' + tag +
                '<span class="unread-badge" id="badge-' + escapeHtml(r.name) + '" style="display:none"></span>' +
                "</span>" +
                "</button></form>";
        }).join("");
        if (Object.keys(_lastUnreadCounts).length > 0) updateUnreadBadges(null);
    }

    function updateTypingIndicator(data) {
        var cfg = getConfig();
        var indicator = document.getElementById("typingIndicator");
        var label = document.getElementById("typingLabel");
        if (!indicator || !label) {
            console.warn("[TYPING] indicator or label element not found in DOM");
            return;
        }

        // SignalR may serialise the list as data.typers or data.Typers depending on
        // serialiser settings — normalise both cases.
        var typerList = data.typers || data.Typers || [];
        console.debug("[TYPING] TypingUpdated received:", data, "typerList:", typerList, "me:", cfg.username);

        // Filter out the current user — typers list now contains display names
        var others = typerList.filter(function (u) {
            return u.toLowerCase() !== cfg.displayName.toLowerCase() &&
                   u.toLowerCase() !== cfg.username.toLowerCase();
        });

        if (others.length === 0) {
            indicator.style.display = "none";
            label.textContent = "";
        } else {
            var text;
            if (others.length === 1) {
                text = others[0] + " is typing…";
            } else if (others.length === 2) {
                text = others[0] + " and " + others[1] + " are typing…";
            } else {
                text = others.length + " people are typing…";
            }
            label.textContent = text;
            indicator.style.display = "flex";
        }
    }

    function escapeHtml(str) {
        return String(str)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    function pad(n) { return n.toString().padStart(2, "0"); }

    // -------------------------------------------------------------------------
    // Heartbeat — keeps _lastSeen fresh on the server
    // -------------------------------------------------------------------------

    function sendHeartbeat() {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
        var cfg = getConfig();
        connection.invoke("Heartbeat", cfg.username, cfg.room).catch(function () { });
    }

    function startHeartbeatTimer() {
        stopHeartbeatTimer();
        sendHeartbeat();
        heartbeatTimer = window.setInterval(sendHeartbeat, HEARTBEAT_INTERVAL_MS);
    }

    function stopHeartbeatTimer() {
        if (heartbeatTimer !== null) { clearInterval(heartbeatTimer); heartbeatTimer = null; }
    }

    // -------------------------------------------------------------------------
    // Connection lifecycle
    // -------------------------------------------------------------------------

    async function startConnection() {
        if (isIntentionallyStopped) return;

        connection = buildConnection();

        connection.on("PresenceUpdated", function (data) {
            // Only update the member list and online count if this event
            // is for the room we currently have open. The server broadcasts
            // PresenceUpdated to the room group, so this should always match —
            // but guard defensively in case of a race during room switches.
            var cfg = getConfig();
            var eventRoom = data.room || data.Room || "";
            if (eventRoom && eventRoom.toLowerCase() !== cfg.room.toLowerCase()) return;
            renderMembers(data);
        });
        connection.on("RabbitStatusUpdated", function (isConnected) { updateRabbitStatus(isConnected); });
        connection.on("UnreadCountsUpdated", function (counts) { updateUnreadBadges(counts); });
        connection.on("RoomsUpdated", function (rooms) { updateRoomList(rooms); });

        // Navigate into a DM room when the server signals us to (after OpenDm)
        connection.on("NavigateToRoom", function (roomName) {
            if (!roomName) return;
            window.location.href = "/Chat?room=" + encodeURIComponent(roomName);
        });

        connection.on("RoomOpened", function (data) {
            // Server sends the authoritative online count for this room on open.
            // PresenceUpdated will keep it live from here; this is just the seed.
            updateOnlineCount(data.usersOnline || 0);

            var cfg = getConfig();

            // Clear this room's unread badge immediately
            var badge = document.getElementById("badge-" + cfg.room);
            if (badge) { badge.style.display = "none"; badge.textContent = ""; }
            _lastUnreadCounts[cfg.room] = 0;

            // Tell the server to mark this room as read
            connection.invoke("MarkRoomRead", cfg.room, cfg.username).catch(function () {});
        });

        connection.on("TypingUpdated", function (data) { updateTypingIndicator(data); });

        connection.on("ReceiveMessage", function (msg) {
            var sender = msg.username || msg.Username || "";
            var text = msg.message || msg.Message || "";

            var cfg = getConfig();
            // Server echoes back displayName as the sender — check against both
            // displayName and username to catch all cases
            var isMe = sender.toLowerCase() === cfg.displayName.toLowerCase() ||
                       sender.toLowerCase() === cfg.username.toLowerCase();

            if (isMe) {
                var dedupKey = sender.toLowerCase() + "|" + text;
                if (_optimisticKeys[dedupKey] > 0) {
                    _optimisticKeys[dedupKey]--;
                    if (_optimisticKeys[dedupKey] === 0) delete _optimisticKeys[dedupKey];
                    return;
                }
            }

            appendMessage(msg);
        });

        // Replaces the panel with the full history pushed by the server on join.
        // This ensures late joiners and users on a different instance see all
        // messages that arrived via RabbitMQ before they connected.
        connection.on("HistoryLoaded", function (items) {
            var panel = document.getElementById("chatMessagePanel");
            if (!panel) return;

            var cfg = getConfig();
            panel.innerHTML = "";

            items.forEach(function (item) {
                // Status events (e.g. "Alice created the room")
                if ((item.itemType || item.ItemType) === "status") {
                    var statusText = item.statusText || item.StatusText || "";
                    if (statusText) {
                        var div = document.createElement("div");
                        div.className = "status-event";
                        div.textContent = statusText;
                        panel.appendChild(div);
                    }
                    return;
                }

                var sender = item.username || item.Username || "";
                var text = item.message || item.Message || "";
                var tsRaw = item.timestampUtc || item.TimestampUtc || new Date().toISOString();
                if (!text) return;

                var isMine        = sender.toLowerCase() === cfg.username.toLowerCase() ||
                                    sender.toLowerCase() === cfg.displayName.toLowerCase();
                var displaySender = isMine ? cfg.displayName : sender;
                var ts            = new Date(tsRaw);
                var timeStr       = pad(ts.getHours()) + ":" + pad(ts.getMinutes()) + ":" + pad(ts.getSeconds());
                var rUser         = item.replyToUsername || item.ReplyToUsername || "";
                var rText         = item.replyToText     || item.ReplyToText     || "";

                panel.insertAdjacentHTML("beforeend",
                    buildMessageHtml(sender, text, timeStr, isMine, rUser, rText, displaySender));
            });

            if (panel.children.length === 0) {
                panel.innerHTML = '<div class="empty-chat-state">No messages yet — be the first to say something!</div>';
            } else {
                panel.scrollTop = panel.scrollHeight;
            }
            // Reveal panel and meta now that content is finalised
            panel.style.visibility = "visible";
            var meta = document.getElementById("chatMeta");
            if (meta) meta.style.visibility = "visible";
            panel.dataset.waitingForHistory = "";
        });

        connection.onreconnecting(function () {
            stopHeartbeatTimer();
        });

        connection.onreconnected(function () {
            var cfg = getConfig();
            var panel = document.getElementById("chatMessagePanel");
            if (panel) panel.innerHTML = "";
            connection.invoke("RegisterConnection", cfg.username, cfg.displayName).catch(function () { });
            connection.invoke("OpenRoom", cfg.room, cfg.username).catch(function () { });
            startHeartbeatTimer();
        });

        connection.onclose(function () {
            stopHeartbeatTimer();
            if (!isIntentionallyStopped) {
                reconnectTimer = window.setTimeout(startConnection, 5000);
            }
        });

        try {
            await connection.start();
            var cfg = getConfig();

            // Wipe the server-rendered message HTML immediately so that when
            // HistoryLoaded arrives it is the only source of truth.
            // This prevents duplicates when the page renders messages server-side
            // AND SignalR pushes the same history via HistoryLoaded.
            var panel = document.getElementById("chatMessagePanel");
            if (panel) {
                panel.innerHTML = "";
                panel.dataset.waitingForHistory = "1";
            }
            // Hide member list until PresenceUpdated replaces server-rendered usernames
            var ml = document.querySelector(".member-list");
            if (ml) ml.style.visibility = "hidden";

            await connection.invoke("RegisterConnection", cfg.username, cfg.displayName);
            await connection.invoke("OpenRoom", cfg.room, cfg.username);
            startHeartbeatTimer();
        } catch (err) {
            console.error("[CHAT] Connection failed:", err);
            // Reveal everything on failure so user sees server-rendered content
            var p = document.getElementById("chatMessagePanel");
            if (p) p.style.visibility = "visible";
            var m = document.getElementById("chatMeta");
            if (m) m.style.visibility = "visible";
            var ml2 = document.querySelector(".member-list");
            if (ml2) ml2.style.visibility = "visible";
            stopHeartbeatTimer();
            if (!isIntentionallyStopped) {
                reconnectTimer = window.setTimeout(startConnection, 5000);
            }
        }
    }

    function stopConnection() {
        isIntentionallyStopped = true;
        stopHeartbeatTimer();
        if (reconnectTimer !== null) { clearTimeout(reconnectTimer); reconnectTimer = null; }
        if (connection) connection.stop();
    }

    // -------------------------------------------------------------------------
    // Typing indicator
    // -------------------------------------------------------------------------
    // The indicator stays active as long as the input has text.
    // It only stops when the user sends the message, clears the field,
    // or leaves the page. No idle timeout — that was causing it to disappear
    // after a couple of seconds while the user was still composing.

    var _isTyping = false;

    function notifyStartTyping() {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
        if (_isTyping) return; // already broadcasting — nothing to do
        _isTyping = true;
        var cfg = getConfig();
        connection.invoke("StartTyping", cfg.room, cfg.username).catch(function (err) {
            console.error("[TYPING] StartTyping failed:", err);
        });
    }

    function notifyStopTyping() {
        if (!_isTyping) return;
        _isTyping = false;
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;
        var cfg = getConfig();
        connection.invoke("StopTyping", cfg.room, cfg.username).catch(function () { });
    }

    // -------------------------------------------------------------------------
    // Send — event delegation so no timing issues.
    //
    // Optimistic rendering: the sender's own message is appended immediately
    // without waiting for the server echo, so it appears instant. The dedup
    // map (_optimisticKeys) prevents a duplicate bubble when the ReceiveMessage
    // echo arrives back from the server.
    // -------------------------------------------------------------------------

    // Tracks messages we've already rendered optimistically (key = "user|text")
    // so the server echo via ReceiveMessage doesn't produce a duplicate bubble.
    // -------------------------------------------------------------------------
    // Reply state
    // -------------------------------------------------------------------------
    var _replyToUsername = "";
    var _replyToText     = "";

    function setReply(sender, text) {
        _replyToUsername = sender;
        _replyToText     = text;
        var preview = document.getElementById("replyPreview");
        var userEl  = document.getElementById("replyPreviewUser");
        var textEl  = document.getElementById("replyPreviewText");
        if (preview) preview.style.display = "flex";
        if (userEl)  userEl.textContent    = sender;
        if (textEl)  textEl.textContent    = text.length > 80 ? text.substring(0, 80) + "…" : text;
        var input = document.getElementById("messageTextInput");
        if (input) input.focus();
    }

    function clearReply() {
        _replyToUsername = "";
        _replyToText     = "";
        var preview = document.getElementById("replyPreview");
        if (preview) preview.style.display = "none";
    }

    var _optimisticKeys = {};

    function trySend() {
        var input = document.getElementById("messageTextInput");
        if (!input) return;
        var text = input.value.trim();
        if (!text) return;
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return;

        var cfg = getConfig();

        // Stop the typing indicator immediately on send
        notifyStopTyping();

        var isReply = !!_replyToUsername;
        var rUser   = _replyToUsername;
        var rText   = _replyToText;

        // Optimistically render own message immediately
        var dedupKey = cfg.displayName.toLowerCase() + "|" + text;
        _optimisticKeys[dedupKey] = (_optimisticKeys[dedupKey] || 0) + 1;
        appendMessage({
            username:        cfg.username,
            message:         text,
            timestampUtc:    new Date().toISOString(),
            replyToUsername: rUser,
            replyToText:     rText
        });

        if (isReply) {
            clearReply();
            connection.invoke("SendReply", cfg.room, cfg.displayName, text, rUser, rText)
                .catch(function (err) { console.error("[CHAT] SendReply failed:", err); });
        } else {
            connection.invoke("SendMessage", cfg.room, cfg.displayName, text)
                .catch(function (err) { console.error("[CHAT] SendMessage failed:", err); });
        }

        input.value = "";
        input.focus();
    }

    document.addEventListener("click", function (e) {
        var t = e.target;
        while (t && t !== document) {
            if (t.id === "sendMessageBtn") { e.preventDefault(); trySend(); return; }

            // Reply button on a message bubble
            if (t.classList && t.classList.contains("reply-btn")) {
                setReply(
                    t.getAttribute("data-sender") || "",
                    t.getAttribute("data-text")   || ""
                );
                return;
            }

            // Cancel reply preview bar
            if (t.id === "cancelReplyBtn") { clearReply(); return; }

            // DM button on a member card
            if (t.classList && t.classList.contains("dm-btn")) {
                var target = t.getAttribute("data-target") || "";
                if (target && connection && connection.state === signalR.HubConnectionState.Connected) {
                    var cfg2 = getConfig();
                    connection.invoke("OpenDm", cfg2.username, target)
                        .catch(function (err) { console.error("[DM] OpenDm failed:", err); });
                }
                return;
            }

            t = t.parentElement;
        }
    });



    // Fire typing notifications on keydown (immediate) AND input (paste/autocomplete).
    // keydown fires before the value changes so we check the current value length + 1.
    document.addEventListener("keydown", function (e) {
        if (!e.target || e.target.id !== "messageTextInput") return;
        if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); trySend(); return; }
        // Any character key, backspace or delete — notify typing
        if (e.key.length === 1 || e.key === "Backspace" || e.key === "Delete") {
            notifyStartTyping();
        }
    });

    document.addEventListener("input", function (e) {
        if (!e.target || e.target.id !== "messageTextInput") return;
        if (e.target.value.trim().length > 0) {
            notifyStartTyping();
        } else {
            notifyStopTyping();
        }
    });

    // -------------------------------------------------------------------------
    // Page unload — use sendBeacon (HTTP) not SignalR invoke.
    //
    // SignalR invoke() is async and the browser closes the WebSocket before the
    // frame can be flushed during beforeunload. navigator.sendBeacon() is the
    // only browser API guaranteed to complete after the page starts unloading.
    // The server handles it via OnPostBeacon (a Razor Page POST handler).
    // -------------------------------------------------------------------------

    window.addEventListener("beforeunload", function () {
        notifyStopTyping();
        var cfg = getConfig();
        var payload = JSON.stringify({ username: cfg.username, room: cfg.room });
        navigator.sendBeacon("/Chat?handler=Beacon", new Blob([payload], { type: "application/json" }));
        stopConnection();
    });

    window.addEventListener("focus", function () {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            sendHeartbeat();
        }
    });

    // Boot
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", startConnection);
    } else {
        startConnection();
    }

})();