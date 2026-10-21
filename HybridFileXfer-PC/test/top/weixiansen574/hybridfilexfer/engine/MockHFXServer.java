package top.weixiansen574.hybridfilexfer.engine;

import java.io.File;
import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.channels.ServerSocketChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.LinkedBlockingDeque;

import top.weixiansen574.hybridfilexfer.core.ControllerIdentifiers;
import top.weixiansen574.hybridfilexfer.core.FileBlock;
import top.weixiansen574.hybridfilexfer.core.HFXService;
import top.weixiansen574.hybridfilexfer.core.ReadFileCall;
import top.weixiansen574.hybridfilexfer.core.TransferConnection;
import top.weixiansen574.hybridfilexfer.core.WriteFileCall;
import top.weixiansen574.hybridfilexfer.core.bean.Directory;
import top.weixiansen574.hybridfilexfer.core.bean.RemoteFile;
import top.weixiansen574.hybridfilexfer.core.callback.TransferFileCallback;
import top.weixiansen574.hybridfilexfer.jdkcore.JdkReadFileCall;
import top.weixiansen574.hybridfilexfer.jdkcore.JdkWriteFileCall;
import top.weixiansen574.nio.DataByteChannel;

/**
 * 仅用于 P1 控制台/协议端到端验证的桌面 Mock 服务端。
 * 它模拟 Android 服务端的 v3.1 握手与 PC 驱动指令，文件系统直接使用本机目录。
 */
public final class MockHFXServer extends HFXService {
    private final ServerSocketChannel serverSocketChannel;
    private final File root;
    private final String homeDir;
    private final int port;
    private final TransferFileCallback transferCallback = new NoopTransferCallback();
    private volatile boolean running = true;
    private volatile boolean closed = false;
    private volatile Throwable error;

    public MockHFXServer(File root) throws IOException {
        this.root = root;
        this.homeDir = root.getAbsolutePath();
        this.serverSocketChannel = ServerSocketChannel.open()
                .bind(new InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0));
        this.port = ((InetSocketAddress) serverSocketChannel.getLocalAddress()).getPort();
    }

    public int getPort() {
        return port;
    }

    public File getRoot() {
        return root;
    }

    public Throwable getError() {
        return error;
    }

    /**
     * 阻塞运行：接受控制通道、握手、接受传输通道、处理命令，直到 SHUTDOWN 或连接断开。
     */
    public void start() {
        try {
            ctChannel = new DataByteChannel(serverSocketChannel.accept());
            byte[] headerBytes = CLIENT_HEADER.getBytes(StandardCharsets.UTF_8);
            byte[] header = new byte[headerBytes.length];
            ctChannel.readFully(header);
            if (!Arrays.equals(headerBytes, header)) {
                throw new IOException("protocol error");
            }
            int version = ctChannel.readInt();
            if (version != VERSION_CODE) {
                ctChannel.writeBoolean(false);
                ctChannel.writeInt(VERSION_CODE);
                throw new IOException("version mismatch: " + version);
            }
            int driveMode = ctChannel.readByte();
            ctChannel.writeBoolean(true);
            ctChannel.writeBoolean(driveMode == DRIVE_MODE_PC);
            if (driveMode != DRIVE_MODE_PC) {
                throw new IOException("mock requires PC drive mode");
            }

            // 单个网卡：MOCK -> 127.0.0.1
            ctChannel.writeInt(1);
            ctChannel.writeUTF("MOCK");
            byte[] address = InetAddress.getByName("127.0.0.1").getAddress();
            ctChannel.writeByte(address.length);
            ctChannel.write(address);
            ctChannel.writeByte(0);

            // 传输通道
            boolean connected = ctChannel.readBoolean();
            String channelName = ctChannel.readUTF();
            if (!connected) {
                throw new IOException("client refused transfer channel " + channelName);
            }
            connections = new ArrayList<>(1);
            connections.add(new TransferConnection(channelName,
                    new DataByteChannel(serverSocketChannel.accept())));
            ctChannel.writeBoolean(true);

            // 缓冲区协商
            int bufferCount = 32;
            ctChannel.writeInt(bufferCount);
            if (!ctChannel.readBoolean()) {
                throw new IOException("client failed to allocate buffers");
            }
            for (int i = 0; i < bufferCount; i++) {
                buffers.add(ByteBuffer.allocateDirect(FileBlock.BLOCK_SIZE));
            }
            ctChannel.writeBoolean(true);

            // 客户端信息
            ctChannel.readInt();//client file system
            ctChannel.readUTF();//client home dir
            // 服务端信息
            ctChannel.writeInt(Directory.getCurrentFileSystem());
            ctChannel.writeUTF(homeDir);

            commandLoop();
        } catch (Throwable e) {
            error = e;
        } finally {
            closeQuietly();
        }
    }

    private void commandLoop() throws IOException {
        while (running) {
            short command = ctChannel.readShort();
            switch (command) {
                case ControllerIdentifiers.PC_LIST_FILES:
                    handleList();
                    break;
                case ControllerIdentifiers.PC_DELETE_FILE:
                    handleDelete();
                    break;
                case ControllerIdentifiers.PC_MKDIR:
                    handleMkdir();
                    break;
                case ControllerIdentifiers.PC_REQUEST_SEND:
                    handleRequestSend();
                    break;
                case ControllerIdentifiers.PC_REQUEST_RECEIVE:
                    handleRequestReceive();
                    break;
                case ControllerIdentifiers.SHUTDOWN:
                    running = false;
                    break;
                default:
                    throw new IOException("unknown command: " + command);
            }
        }
    }

    private void handleList() throws IOException {
        String path = ctChannel.readUTF();
        File[] files = new File(path).listFiles();
        if (files == null) {
            ctChannel.writeInt(-1);
            return;
        }
        ctChannel.writeInt(files.length);
        for (File file : files) {
            writeRemoteFile(ctChannel, new RemoteFile(file));
        }
    }

    private void handleDelete() throws IOException {
        String path = ctChannel.readUTF();
        ctChannel.writeBoolean(deleteRecursively(new File(path)));
    }

    private void handleMkdir() throws IOException {
        String parent = ctChannel.readUTF();
        String child = ctChannel.readUTF();
        ctChannel.writeBoolean(new File(parent, child).mkdirs());
    }

    private void handleRequestSend() throws IOException {
        int count = ctChannel.readInt();
        List<RemoteFile> files = new ArrayList<>(count);
        for (int i = 0; i < count; i++) {
            files.add(new RemoteFile(new File(ctChannel.readUTF())));
        }
        Directory localDir = readDirectory(ctChannel);
        Directory remoteDir = readDirectory(ctChannel);
        sendFiles(files, localDir, remoteDir, transferCallback);
    }

    private void handleRequestReceive() throws IOException {
        readDirectory(ctChannel);//PC source
        readDirectory(ctChannel);//mock target
        receiveFiles(transferCallback);
    }

    private boolean deleteRecursively(File file) {
        if (!file.exists()) {
            return false;
        }
        if (file.isDirectory()) {
            File[] children = file.listFiles();
            if (children != null) {
                for (File child : children) {
                    deleteRecursively(child);
                }
            }
        }
        return file.delete();
    }

    public void stop() {
        running = false;
        closeQuietly();
    }

    private synchronized void closeQuietly() {
        if (closed) {
            return;
        }
        closed = true;
        running = false;
        for (ByteBuffer buffer : buffers) {
            // DirectByteBuffer 由 GC 回收；这里只清空队列
            buffer.clear();
        }
        buffers.clear();
        if (ctChannel != null) {
            try {
                ctChannel.close();
            } catch (IOException ignored) {
            }
        }
        if (connections != null) {
            for (TransferConnection connection : connections) {
                try {
                    connection.close();
                } catch (IOException ignored) {
                }
            }
            connections.clear();
        }
        try {
            serverSocketChannel.close();
        } catch (IOException ignored) {
        }
    }

    @Override
    protected WriteFileCall createWriteFileCall(LinkedBlockingDeque<ByteBuffer> buffers, int dequeCount) {
        return new JdkWriteFileCall(buffers, dequeCount);
    }

    @Override
    protected ReadFileCall createReadFileCall(LinkedBlockingDeque<ByteBuffer> buffers,
                                               List<RemoteFile> files, Directory localDir,
                                               Directory remoteDir, int operateThreadCount) {
        return new JdkReadFileCall(buffers, files, localDir, remoteDir, operateThreadCount);
    }

    private static final class NoopTransferCallback implements TransferFileCallback {
        @Override
        public void onFileUploading(String iName, String path, long targetSize, long totalSize) {
        }

        @Override
        public void onFileDownloading(String iName, String path, long targetSize, long totalSize) {
        }

        @Override
        public void onSpeedInfo(List<top.weixiansen574.hybridfilexfer.core.bean.TrafficInfo> trafficInfoList) {
        }

        @Override
        public void onChannelComplete(String iName, long traffic, long time) {
        }

        @Override
        public void onChannelError(String iName, int errorType, String message) {
        }

        @Override
        public void onReadFileError(String message) {
        }

        @Override
        public void onWriteFileError(String message) {
        }

        @Override
        public void onComplete(boolean isUpload, long traffic, long time) {
        }

        @Override
        public void onIncomplete() {
        }
    }
}
