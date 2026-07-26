var ETMiniGameWebSocketLibrary = {
    $ETMiniGameWebSocketManager: {
        nextId: 1,
        sockets: {},
        onOpen: 0,
        onMessage: 0,
        onError: 0,
        onClose: 0,

        invokeString: function(callback, socketId, value, extra) {
            var text = value || "";
            var size = lengthBytesUTF8(text) + 1;
            var pointer = _malloc(size);
            stringToUTF8(text, pointer, size);
            try {
                if (typeof extra === "number") {
                    Module.dynCall_viii(callback, socketId, extra, pointer);
                } else {
                    Module.dynCall_vii(callback, socketId, pointer);
                }
            } finally {
                _free(pointer);
            }
        }
    },

    ETMiniGameWebSocketSetCallbacks: function(onOpen, onMessage, onError, onClose) {
        ETMiniGameWebSocketManager.onOpen = onOpen;
        ETMiniGameWebSocketManager.onMessage = onMessage;
        ETMiniGameWebSocketManager.onError = onError;
        ETMiniGameWebSocketManager.onClose = onClose;
    },

    ETMiniGameWebSocketCreate: function(urlPointer) {
        var socketId = ETMiniGameWebSocketManager.nextId++;
        ETMiniGameWebSocketManager.sockets[socketId] = {
            url: UTF8ToString(urlPointer),
            socket: null
        };
        return socketId;
    },

    ETMiniGameWebSocketConnect: function(socketId) {
        var state = ETMiniGameWebSocketManager.sockets[socketId];
        if (!state) return -1;
        if (state.socket) return -2;

        try {
            var socket = new WebSocket(state.url);
            state.socket = socket;
            socket.binaryType = "arraybuffer";

            socket.onopen = function() {
                Module.dynCall_vi(ETMiniGameWebSocketManager.onOpen, socketId);
            };

            socket.onmessage = function(event) {
                if (!(event.data instanceof ArrayBuffer)) {
                    ETMiniGameWebSocketManager.invokeString(
                        ETMiniGameWebSocketManager.onError,
                        socketId,
                        "WebSocket server returned a non-binary frame.");
                    return;
                }

                var bytes = new Uint8Array(event.data);
                var pointer = _malloc(bytes.length);
                HEAPU8.set(bytes, pointer);
                try {
                    Module.dynCall_viii(
                        ETMiniGameWebSocketManager.onMessage,
                        socketId,
                        pointer,
                        bytes.length);
                } finally {
                    _free(pointer);
                }
            };

            socket.onerror = function() {
                ETMiniGameWebSocketManager.invokeString(
                    ETMiniGameWebSocketManager.onError,
                    socketId,
                    "WebSocket connection error.");
            };

            socket.onclose = function(event) {
                ETMiniGameWebSocketManager.invokeString(
                    ETMiniGameWebSocketManager.onClose,
                    socketId,
                    event.reason || "",
                    event.code || 1006);
                state.socket = null;
            };
            return 0;
        } catch (error) {
            state.socket = null;
            return -3;
        }
    },

    ETMiniGameWebSocketSend: function(socketId, dataPointer, length) {
        var state = ETMiniGameWebSocketManager.sockets[socketId];
        if (!state) return -1;
        if (!state.socket || state.socket.readyState !== WebSocket.OPEN) return -2;

        var copy = HEAPU8.slice(dataPointer, dataPointer + length);
        state.socket.send(copy.buffer);
        return 0;
    },

    ETMiniGameWebSocketClose: function(socketId) {
        var state = ETMiniGameWebSocketManager.sockets[socketId];
        if (!state || !state.socket) return;
        if (state.socket.readyState === WebSocket.CONNECTING ||
            state.socket.readyState === WebSocket.OPEN) {
            state.socket.close(1000, "Client shutdown");
        }
    },

    ETMiniGameWebSocketFree: function(socketId) {
        delete ETMiniGameWebSocketManager.sockets[socketId];
    }
};

autoAddDeps(ETMiniGameWebSocketLibrary, "$ETMiniGameWebSocketManager");
mergeInto(LibraryManager.library, ETMiniGameWebSocketLibrary);
