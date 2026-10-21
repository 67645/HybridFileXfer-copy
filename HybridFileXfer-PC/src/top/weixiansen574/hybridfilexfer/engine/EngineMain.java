package top.weixiansen574.hybridfilexfer.engine;

import java.io.BufferedReader;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicReference;

import top.weixiansen574.hybridfilexfer.Main;
import top.weixiansen574.hybridfilexfer.core.HFXService;
import top.weixiansen574.hybridfilexfer.core.bean.Directory;
import top.weixiansen574.hybridfilexfer.core.bean.RemoteFile;
import top.weixiansen574.hybridfilexfer.core.bean.TrafficInfo;
import top.weixiansen574.hybridfilexfer.core.callback.ConnectServerCallback;
import top.weixiansen574.hybridfilexfer.core.callback.TransferFileCallback;
import top.weixiansen574.hybridfilexfer.jdkcore.JdkHFXClient;

/**
 * GUI 引擎模式：stdin 读 JSON Lines 指令，stdout 写 JSON Lines 事件。
 * 日志也以 JSON 事件输出，stdout 不输出任何非 JSON 内容。
 */
public final class EngineMain {
    private static final long PROGRESS_INTERVAL_MS = 100;

    private EngineMain() {
    }

    public static void run(InputStream in, PrintStream out) {
        new Engine(out).loop(new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8)));
    }

    private static final class Engine {
        private final PrintStream out;
        private JdkHFXClient client;
        private boolean running = true;
        private long lastProgressEmit = 0;

        Engine(PrintStream out) {
            this.out = out;
        }

        void loop(BufferedReader reader) {
            emit("{\"event\":\"ready\",\"protocol\":" + HFXService.VERSION_CODE
                    + ",\"java\":" + Json.quote(System.getProperty("java.version")) + "}");
            emitState("idle");
            try {
                String line;
                while (running && (line = reader.readLine()) != null) {
                    line = line.trim();
                    if (line.isEmpty()) {
                        continue;
                    }
                    Object id = null;
                    try {
                        Map<String, Object> request = Json.parseObject(line);
                        id = request.get("id");
                        String command = Json.string(request, "cmd");
                        dispatch(command, request, id);
                    } catch (Exception e) {
                        emitError(id, e.toString());
                    }
                }
            } catch (IOException e) {
                emitError(null, "stdin error: " + e);
            } finally {
                disconnectClient();
            }
        }

        private void dispatch(String command, Map<String, Object> request, Object id) throws Exception {
            if (command == null) {
                throw new IllegalArgumentException("missing cmd");
            }
            switch (command) {
                case "connect":
                    handleConnect(request, id);
                    break;
                case "disconnect":
                    disconnectClient();
                    emitState("disconnected");
                    emitResult(id, true);
                    break;
                case "shutdown":
                    disconnectClient();
                    emitState("bye");
                    emitResult(id, true);
                    running = false;
                    break;
                case "ping":
                    emitState(client == null ? "idle" : "connected");
                    emitResult(id, true);
                    break;
                case "list":
                    handleList(request, id);
                    break;
                case "delete":
                    handleDelete(request, id);
                    break;
                case "mkdir":
                    handleMkdir(request, id);
                    break;
                case "upload":
                    handleUpload(request, id);
                    break;
                case "download":
                    handleDownload(request, id);
                    break;
                default:
                    throw new IllegalArgumentException("unknown cmd: " + command);
            }
        }

        private void handleConnect(Map<String, Object> request, Object id) throws Exception {
            if (client != null) {
                throw new IllegalStateException("already connected");
            }
            String mode = Json.string(request, "mode", "ip");
            String address = Json.string(request, "address");
            String device = Json.string(request, "device", "");
            String homeDir = Json.string(request, "homeDir", System.getProperty("user.dir"));
            int port = Json.intValue(request, "port", 5740);
            int driveMode = Json.intValue(request, "driveMode", HFXService.DRIVE_MODE_PC);

            if ("adb".equalsIgnoreCase(mode)) {
                emitState("adb-forwarding");
                boolean forwarded = Main.executeAdbForwardCommand(port, device.isEmpty() ? null : device,
                        message -> emitLog("adb", message), message -> emitLog("adb", message));
                if (!forwarded) {
                    throw new IOException("adb forward failed");
                }
                address = "127.0.0.1";
            }
            if (address == null || address.isEmpty()) {
                throw new IllegalArgumentException("missing address");
            }

            JdkHFXClient newClient = new JdkHFXClient(address, port, homeDir, driveMode);
            final String[] connectError = new String[1];
            final AtomicReference<List<String>> connectedChannels = new AtomicReference<>();
            ConnectServerCallback callback = new ConnectServerCallback() {
                @Override
                public void onConnectingControlChannel(String address, int port) {
                    emitState("connecting-control");
                    emitLog("connect", "control channel " + address + ":" + port);
                }

                @Override
                public void onVersionMismatch(int localVersion, int remoteVersion) {
                    connectError[0] = "protocol version mismatch: local=" + localVersion
                            + ", remote=" + remoteVersion;
                    emitError(id, connectError[0]);
                }

                @Override
                public void onConnectControlFailed() {
                    connectError[0] = "control channel connection failed";
                    emitError(id, connectError[0]);
                }

                @Override
                public void onConnectingTransferChannel(String name, InetAddress inetAddress,
                                                        InetAddress bindAddress) {
                    emitLog("connect", "transfer channel " + name + " -> "
                            + inetAddress.getHostAddress() + " bind="
                            + (bindAddress == null ? "null" : bindAddress.getHostAddress()));
                }

                @Override
                public void onConnectTransferChannelFailed(String name, InetAddress inetAddress,
                                                           Exception e) {
                    connectError[0] = "transfer channel failed: " + name + " " + e;
                    emitError(id, connectError[0]);
                }

                @Override
                public void onOOM(int createdBuffers, int requiredBuffers, long maxMemoryMB,
                                  String osArch) {
                    connectError[0] = "local out of memory: created " + createdBuffers
                            + "/" + requiredBuffers + " buffers";
                    emitError(id, connectError[0]);
                }

                @Override
                public void onRemoteOOM() {
                    connectError[0] = "remote out of memory";
                    emitError(id, connectError[0]);
                }

                @Override
                public void onConnectSuccess(List<String> channelNames) {
                    connectedChannels.set(channelNames);
                }

                @Override
                public void onDriveModeRejected() {
                    connectError[0] = "phone rejected PC control mode";
                    emitError(id, connectError[0]);
                }
            };

            try {
                if (!newClient.connect(callback)) {
                    newClient.freeBuffers();
                    throw new IOException(connectError[0] == null ? "connect failed" : connectError[0]);
                }
            } catch (IOException e) {
                newClient.freeBuffers();
                throw e;
            }
            client = newClient;
            emitConnected(id, newClient, connectedChannels.get());
        }

        private void handleList(Map<String, Object> request, Object id) throws IOException {
            requireClient();
            String path = Json.string(request, "path", client.getServerHomeDir());
            List<RemoteFile> files = client.listServerFiles(path);
            if (files == null) {
                emitFiles(id, path, new ArrayList<>(), "unavailable");
                return;
            }
            emitFiles(id, path, files, null);
        }

        private void handleDelete(Map<String, Object> request, Object id) throws IOException {
            requireClient();
            String path = Json.string(request, "path");
            if (path == null || path.isEmpty()) {
                throw new IllegalArgumentException("missing path");
            }
            emitResult(id, client.deleteServerFile(path));
        }

        private void handleMkdir(Map<String, Object> request, Object id) throws IOException {
            requireClient();
            String parent = Json.string(request, "parent");
            String child = Json.string(request, "child");
            if (parent == null || parent.isEmpty() || child == null || child.isEmpty()) {
                throw new IllegalArgumentException("missing parent or child");
            }
            emitResult(id, client.mkdirServer(parent, child));
        }

        private void handleUpload(Map<String, Object> request, Object id) throws IOException {
            requireClient();
            List<String> paths = Json.stringList(request, "paths");
            String sourceDir = Json.string(request, "sourceDir");
            String targetDir = Json.string(request, "targetDir");
            if (paths.isEmpty()) {
                throw new IllegalArgumentException("empty paths");
            }
            if (sourceDir == null || sourceDir.isEmpty() || targetDir == null || targetDir.isEmpty()) {
                throw new IllegalArgumentException("missing sourceDir or targetDir");
            }
            List<RemoteFile> files = new ArrayList<>(paths.size());
            for (String path : paths) {
                files.add(new RemoteFile(new File(path)));
            }
            Directory source = new Directory(sourceDir, Directory.getCurrentFileSystem());
            Directory target = new Directory(targetDir, client.getServerFileSystem());
            emitState("uploading");
            boolean success = client.uploadFiles(files, source, target, transferCallback("upload"));
            emitResult(id, success);
        }

        private void handleDownload(Map<String, Object> request, Object id) throws IOException {
            requireClient();
            List<String> paths = Json.stringList(request, "paths");
            String sourceDir = Json.string(request, "sourceDir");
            String targetDir = Json.string(request, "targetDir");
            if (paths.isEmpty()) {
                throw new IllegalArgumentException("empty paths");
            }
            if (sourceDir == null || sourceDir.isEmpty() || targetDir == null || targetDir.isEmpty()) {
                throw new IllegalArgumentException("missing sourceDir or targetDir");
            }
            List<RemoteFile> files = new ArrayList<>(paths.size());
            for (String path : paths) {
                files.add(new RemoteFile(path, path, 0, 0, false));
            }
            Directory source = new Directory(sourceDir, client.getServerFileSystem());
            Directory target = new Directory(targetDir, Directory.getCurrentFileSystem());
            emitState("downloading");
            boolean success = client.downloadFiles(files, source, target, transferCallback("download"));
            emitResult(id, success);
        }

        private TransferFileCallback transferCallback(String direction) {
            return new TransferFileCallback() {
                @Override
                public void onFileUploading(String iName, String path, long targetSize, long totalSize) {
                    emitProgress(direction, path, targetSize, totalSize);
                }

                @Override
                public void onFileDownloading(String iName, String path, long targetSize, long totalSize) {
                    emitProgress(direction, path, targetSize, totalSize);
                }

                @Override
                public void onSpeedInfo(List<TrafficInfo> trafficInfoList) {
                    emitSpeed(trafficInfoList);
                }

                @Override
                public void onChannelComplete(String iName, long traffic, long time) {
                    emitChannel(iName, "complete", traffic, time, null);
                }

                @Override
                public void onChannelError(String iName, int errorType, String message) {
                    emitChannel(iName, "error", 0, 0, message == null ? ("type " + errorType) : message);
                }

                @Override
                public void onReadFileError(String message) {
                    emitError(null, "read file error: " + message);
                }

                @Override
                public void onWriteFileError(String message) {
                    emitError(null, "write file error: " + message);
                }

                @Override
                public void onComplete(boolean isUpload, long traffic, long time) {
                    emit("{\"event\":\"complete\",\"direction\":" + Json.quote(direction)
                            + ",\"traffic\":" + traffic + ",\"time\":" + time + "}");
                    emitState("idle");
                }

                @Override
                public void onIncomplete() {
                    emitError(null, "transfer incomplete");
                    emitState("idle");
                }
            };
        }

        private void disconnectClient() {
            if (client != null) {
                client.shutdownServer();
                client = null;
            }
        }

        private void requireClient() {
            if (client == null) {
                throw new IllegalStateException("not connected");
            }
        }

        private void emitConnected(Object id, JdkHFXClient connectedClient, List<String> channels) {
            StringBuilder builder = new StringBuilder("{\"event\":\"connected\"");
            builder.append(idPart(id));
            builder.append(",\"channels\":[");
            if (channels != null) {
                for (int i = 0; i < channels.size(); i++) {
                    if (i > 0) {
                        builder.append(',');
                    }
                    builder.append(Json.quote(channels.get(i)));
                }
            }
            builder.append("],\"serverHomeDir\":")
                    .append(Json.quote(connectedClient.getServerHomeDir()))
                    .append(",\"serverFileSystem\":")
                    .append(connectedClient.getServerFileSystem())
                    .append('}');
            emit(builder.toString());
            emitState("connected");
        }

        private void emitFiles(Object id, String path, List<RemoteFile> files, String error) {
            StringBuilder builder = new StringBuilder("{\"event\":\"files\"");
            builder.append(idPart(id));
            builder.append(",\"path\":").append(Json.quote(path)).append(",\"entries\":[");
            for (int i = 0; i < files.size(); i++) {
                RemoteFile file = files.get(i);
                if (i > 0) {
                    builder.append(',');
                }
                builder.append("{\"name\":").append(Json.quote(file.getName()))
                        .append(",\"path\":").append(Json.quote(file.getPath()))
                        .append(",\"lastModified\":").append(file.lastModified())
                        .append(",\"size\":").append(file.getSize())
                        .append(",\"isDirectory\":").append(file.isDirectory())
                        .append('}');
            }
            builder.append(']');
            if (error != null) {
                builder.append(",\"error\":").append(Json.quote(error));
            }
            builder.append('}');
            emit(builder.toString());
        }

        private void emitProgress(String direction, String path, long current, long total) {
            long now = System.currentTimeMillis();
            if (current < total && now - lastProgressEmit < PROGRESS_INTERVAL_MS) {
                return;
            }
            lastProgressEmit = now;
            emit("{\"event\":\"progress\",\"direction\":" + Json.quote(direction)
                    + ",\"path\":" + Json.quote(path)
                    + ",\"current\":" + current
                    + ",\"total\":" + total + "}");
        }

        private void emitSpeed(List<TrafficInfo> trafficInfoList) {
            long upload = 0;
            long download = 0;
            StringBuilder builder = new StringBuilder("{\"event\":\"speed\",\"channels\":[");
            for (int i = 0; i < trafficInfoList.size(); i++) {
                TrafficInfo info = trafficInfoList.get(i);
                upload += info.uploadTraffic;
                download += info.downloadTraffic;
                if (i > 0) {
                    builder.append(',');
                }
                builder.append("{\"name\":").append(Json.quote(info.iName))
                        .append(",\"upload\":").append(info.uploadTraffic)
                        .append(",\"download\":").append(info.downloadTraffic)
                        .append('}');
            }
            builder.append("],\"upload\":").append(upload)
                    .append(",\"download\":").append(download)
                    .append('}');
            emit(builder.toString());
        }

        private void emitChannel(String name, String state, long traffic, long time, String message) {
            StringBuilder builder = new StringBuilder("{\"event\":\"channel\",\"name\":")
                    .append(Json.quote(name))
                    .append(",\"state\":").append(Json.quote(state))
                    .append(",\"traffic\":").append(traffic)
                    .append(",\"time\":").append(time);
            if (message != null) {
                builder.append(",\"message\":").append(Json.quote(message));
            }
            builder.append('}');
            emit(builder.toString());
        }

        private void emitResult(Object id, boolean success) {
            emit("{\"event\":\"result\"" + idPart(id) + ",\"success\":" + success + "}");
        }

        private void emitState(String state) {
            emit("{\"event\":\"state\",\"state\":" + Json.quote(state) + "}");
        }

        private void emitError(Object id, String message) {
            emit("{\"event\":\"error\"" + idPart(id) + ",\"message\":" + Json.quote(message) + "}");
        }

        private void emitLog(String source, String message) {
            emit("{\"event\":\"log\",\"source\":" + Json.quote(source)
                    + ",\"message\":" + Json.quote(message) + "}");
        }

        private String idPart(Object id) {
            return id == null ? "" : ",\"id\":" + id;
        }

        private void emit(String json) {
            out.println(json);
            out.flush();
        }
    }
}
