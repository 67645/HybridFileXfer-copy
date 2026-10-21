package top.weixiansen574.hybridfilexfer.droidcore;

import android.os.Environment;
import android.os.RemoteException;

import java.io.File;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.channels.ServerSocketChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.FutureTask;
import java.util.concurrent.LinkedBlockingDeque;

import top.weixiansen574.async.BackstageTask;
import top.weixiansen574.hybridfilexfer.NativeMemory;
import top.weixiansen574.hybridfilexfer.aidl.IIOService;
import top.weixiansen574.hybridfilexfer.core.ControllerIdentifiers;
import top.weixiansen574.hybridfilexfer.core.FileBlock;
import top.weixiansen574.hybridfilexfer.core.HFXService;
import top.weixiansen574.hybridfilexfer.core.ReadFileCall;
import top.weixiansen574.hybridfilexfer.core.ReceiveFileCall;
import top.weixiansen574.hybridfilexfer.core.SendFileCall;
import top.weixiansen574.hybridfilexfer.core.SpeedMonitorThread;
import top.weixiansen574.hybridfilexfer.core.TransferConnection;
import top.weixiansen574.hybridfilexfer.core.WriteFileCall;
import top.weixiansen574.hybridfilexfer.core.bean.Directory;
import top.weixiansen574.hybridfilexfer.core.bean.RemoteFile;
import top.weixiansen574.hybridfilexfer.core.bean.ServerNetInterface;
import top.weixiansen574.hybridfilexfer.core.bean.TrafficInfo;
import top.weixiansen574.hybridfilexfer.droidcore.callback.StartServerCallback;
import top.weixiansen574.hybridfilexfer.core.callback.TransferFileCallback;
import top.weixiansen574.nio.DataByteChannel;

public class HFXServer extends HFXService {
    public static HFXServer instance;
    protected final IIOService ioService;
    protected ServerSocketChannel serverSocketChannel;
    protected int remoteFileSystem;
    protected String remoteHomeDir;
    protected int clientVersion = VERSION_CODE;
    protected int driveMode = DRIVE_MODE_PHONE;
    protected String serverHomeDir = "/";
    private volatile boolean driveModeRunning = false;
    private volatile boolean disconnectRequestedByUi = false;
    private boolean closed = false;
    private Thread commandThread;
    private StartServerCallback serverCallback;

    public HFXServer(IIOService ioService) {
        this.ioService = ioService;
    }

    public void startServer(int port, List<ServerNetInterface> interfaceList, int localBufferCount,
                            int remoteBufferCount, boolean pcDriveAllowed,
                            StartServerCallback callback) throws IOException {
        ServerSocketChannel serverSocketChannel;
        try {
            serverSocketChannel = ServerSocketChannel.open().bind(new InetSocketAddress(port));
        } catch (IOException e) {
            callback.onBindFailed(port);
            return;
        }
        this.serverSocketChannel = serverSocketChannel;
        callback.onStatedServer();
        //控制通道
        DataByteChannel ctChannel;
        //传输通道
        List<TransferConnection> connections;
        //第一此accept的为控制通道
        conn:
        while (true) {
            //协议判断
            ctChannel = new DataByteChannel(serverSocketChannel.accept());
            byte[] headerBytes = HFXServer.CLIENT_HEADER.getBytes(StandardCharsets.UTF_8);
            byte[] header = new byte[headerBytes.length];
            ctChannel.readFully(header);
            if (!Arrays.equals(headerBytes, header)) {
                ctChannel.writeBytes("protocol error\n");
                ctChannel.close();
                continue;
            }
            //版本判断：v3.1 同时兼容旧版 v3.0
            int versionCode = ctChannel.readInt();
            boolean legacy = versionCode == VERSION_CODE_LEGACY;
            if (versionCode != VERSION_CODE && !legacy) {
                //返回当前服务端版本信息
                ctChannel.writeBoolean(false);//版本未正确匹配
                ctChannel.writeInt(VERSION_CODE);
                ctChannel.close();
                continue;
            }
            ctChannel.writeBoolean(true);//版本正确匹配
            int driveMode = DRIVE_MODE_PHONE;
            if (!legacy) {
                //v3.1：读取客户端驱动模式
                driveMode = ctChannel.readByte();
                if (driveMode != DRIVE_MODE_PHONE && driveMode != DRIVE_MODE_PC) {
                    ctChannel.writeBoolean(false);
                    ctChannel.close();
                    continue conn;
                }
                //PC 控制模式需要手机端允许
                boolean driveModeAllowed = driveMode == DRIVE_MODE_PHONE || pcDriveAllowed;
                ctChannel.writeBoolean(driveModeAllowed);
                if (!driveModeAllowed) {
                    ctChannel.close();
                    callback.onPcDriveModeRejected();
                    continue conn;
                }
            }
            this.clientVersion = versionCode;
            this.driveMode = driveMode;
            ctChannel.writeInt(interfaceList.size());//网卡IP数量
            for (ServerNetInterface netInterface : interfaceList) {
                byte[] address = netInterface.address.getAddress();
                ctChannel.writeUTF(netInterface.name);//网卡名称
                ctChannel.writeByte(address.length);//地址长度（IPv4：4与IPv6：16）
                ctChannel.write(address);//地址
                if (netInterface.clientBindAddress == null) {
                    ctChannel.writeByte(0);//地址长度（null:0）
                } else {
                    byte[] bAddress = netInterface.clientBindAddress.getAddress();
                    ctChannel.writeByte(bAddress.length);//地址长度（IPv4：4与IPv6：16）
                    ctChannel.write(bAddress);//地址
                }
            }
            //连接传输通道
            connections = new ArrayList<>(interfaceList.size());
            for (int i = 0; i < interfaceList.size(); i++) {
                //对方连接通道是否成功
                boolean succeed = ctChannel.readBoolean();
                //对方所连接通道的名称（由控制器通道发送名称）
                String name = ctChannel.readUTF();
                if (succeed) {
                    //accept通道
                    connections.add(new TransferConnection(name, new DataByteChannel(serverSocketChannel.accept())));
                    ctChannel.writeBoolean(true);
                    callback.onAccepted(name);
                } else {
                    ctChannel.writeBoolean(false);
                    ctChannel.close();
                    callback.onAcceptFailed(name);
                    continue conn;
                }
            }
            break;
        }

        LinkedBlockingDeque<ByteBuffer> buffers = this.buffers;
        //告知对方创建的缓冲区块数量
        ctChannel.writeInt(remoteBufferCount);
        if (!ctChannel.readBoolean()) {
            callback.onPcOOM();
            serverSocketChannel.close();
            return;
        }
        //创建缓冲区块
        for (int i = 0; i < localBufferCount; i++) {
            ByteBuffer buffer = NativeMemory.allocateLargeBuffer(FileBlock.BLOCK_SIZE);
            if (buffer != null) {
                buffers.add(buffer);
            } else {
                //释放缓冲区块内存
                for (ByteBuffer buff : buffers) {
                    NativeMemory.freeBuffer(buff);
                }
                buffers.clear();
                ctChannel.writeBoolean(false);
                serverSocketChannel.close();
                callback.onMeOOM(i, localBufferCount);
                return;
            }
        }
        ctChannel.writeBoolean(true);
        //读取对方文件系统信息
        this.remoteFileSystem = ctChannel.readInt();
        //读取对方设定的主目录
        this.remoteHomeDir = ctChannel.readUTF();
        //v3.1：把自己的文件系统与主目录返回给对方
        this.serverHomeDir = Environment.getExternalStorageDirectory().getAbsolutePath();
        if (this.clientVersion != VERSION_CODE_LEGACY) {
            ctChannel.writeInt(Directory.getCurrentFileSystem());
            ctChannel.writeUTF(serverHomeDir);
        }
        this.ctChannel = ctChannel;
        this.connections = connections;
        this.serverCallback = callback;
        callback.onConnectSuccess();
        if (this.driveMode == DRIVE_MODE_PC) {
            callback.onPcDriveModeStarted();
            driveModeRunning = true;
            commandThread = new Thread(this::commandLoop, "ServerCommand");
            commandThread.start();
        }
    }

    /**
     * v3.1 PC 驱动模式：单线程处理客户端指令。
     * 传输期间不处理新指令，确保控制通道一问一答。
     */
    private void commandLoop() {
        String reason = null;
        try {
            while (driveModeRunning) {
                short command = ctChannel.readShort();
                switch (command) {
                    case ControllerIdentifiers.PC_LIST_FILES:
                        handlePcListFiles();
                        break;
                    case ControllerIdentifiers.PC_DELETE_FILE:
                        handlePcDeleteFile();
                        break;
                    case ControllerIdentifiers.PC_MKDIR:
                        handlePcMkdir();
                        break;
                    case ControllerIdentifiers.PC_REQUEST_SEND:
                        handlePcRequestSend();
                        break;
                    case ControllerIdentifiers.PC_REQUEST_RECEIVE:
                        handlePcRequestReceive();
                        break;
                    case ControllerIdentifiers.SHUTDOWN:
                        reason = "client shutdown";
                        driveModeRunning = false;
                        break;
                    default:
                        throw new IOException("unknown PC command: " + command);
                }
            }
        } catch (Exception e) {
            reason = e.toString();
        } finally {
            driveModeRunning = false;
            disconnect();
            if (!disconnectRequestedByUi && serverCallback != null) {
                serverCallback.onPcDriveModeStopped(reason);
            }
        }
    }

    private void handlePcListFiles() throws IOException {
        String path = ctChannel.readUTF();
        List<RemoteFile> files;
        try {
            files = listLocalFiles(path);
        } catch (RemoteException e) {
            files = null;
        }
        if (files == null) {
            ctChannel.writeInt(-1);
            return;
        }
        ctChannel.writeInt(files.size());
        for (RemoteFile file : files) {
            writeRemoteFile(ctChannel, file);
        }
    }

    private void handlePcDeleteFile() throws IOException, RemoteException {
        String path = ctChannel.readUTF();
        ctChannel.writeBoolean(deleteLocalFile(path));
    }

    private void handlePcMkdir() throws IOException, RemoteException {
        String parent = ctChannel.readUTF();
        String child = ctChannel.readUTF();
        ctChannel.writeBoolean(createLocalDir(parent, child));
    }

    /**
     * PC 请求手机发送文件到 PC。
     * 客户端会同时进入 receiveFiles，完成确认由现有控制通道协议处理。
     */
    private void handlePcRequestSend() throws IOException {
        int listSize = ctChannel.readInt();
        List<RemoteFile> fileList = new ArrayList<>(listSize);
        for (int i = 0; i < listSize; i++) {
            fileList.add(new RemoteFile(new File(ctChannel.readUTF())));
        }
        Directory localDir = readDirectory(ctChannel);//手机端源目录
        Directory remoteDir = readDirectory(ctChannel);//PC 端目标目录
        sendFiles(fileList, localDir, remoteDir, commandTransferCallback);
    }

    /**
     * PC 请求向手机发送文件。
     * 客户端会同时进入 sendFiles，完成确认由现有控制通道协议处理。
     */
    private void handlePcRequestReceive() throws IOException {
        readDirectory(ctChannel);//PC 端源目录，当前接收端不需要
        readDirectory(ctChannel);//手机端目标目录，负责生成路径的是发送端
        receiveFiles(commandTransferCallback);
    }

    /**
     * PC 驱动模式下的服务端传输回调。
     * 进度由 PC 侧回调展示，这里只记录错误与完成信息。
     */
    private final TransferFileCallback commandTransferCallback = new TransferFileCallback() {
        @Override
        public void onFileUploading(String iName, String path, long targetSize, long totalSize) {
        }

        @Override
        public void onFileDownloading(String iName, String path, long targetSize, long totalSize) {
        }

        @Override
        public void onSpeedInfo(List<TrafficInfo> trafficInfoList) {
        }

        @Override
        public void onChannelComplete(String iName, long traffic, long time) {
        }

        @Override
        public void onChannelError(String iName, int errorType, String message) {
            System.err.println("PC drive channel error: " + iName + " type=" + errorType + " " + message);
        }

        @Override
        public void onReadFileError(String message) {
            System.err.println("PC drive read error: " + message);
        }

        @Override
        public void onWriteFileError(String message) {
            System.err.println("PC drive write error: " + message);
        }

        @Override
        public void onComplete(boolean isUpload, long traffic, long time) {
        }

        @Override
        public void onIncomplete() {
            System.err.println("PC drive transfer incomplete");
        }
    };

    public void closeServerSocket() {
        if (serverSocketChannel != null) {
            try {
                serverSocketChannel.close();
            } catch (IOException e) {
                e.printStackTrace();
            }
        }
    }

    public void disconnect(BackstageTask.BaseEventHandler callback) {
        disconnectRequestedByUi = true;
        new DisconnectTask(callback, this).execute();
    }

    public int getRemoteFileSystem() {
        return remoteFileSystem;
    }

    public boolean isPcDriveMode() {
        return driveMode == DRIVE_MODE_PC;
    }

    public String getRemoteHomeDir() {
        return remoteHomeDir;
    }

    public void sendFilesToRemote(List<RemoteFile> files, Directory localDir, Directory remoteDir, TransferFileCallback callback) throws IOException {
        ctChannel.writeShort(ControllerIdentifiers.REQUEST_RECEIVE);//请求对方接收
        sendFiles(files,localDir,remoteDir,callback);
    }

    public void sendFilesToShelf(List<RemoteFile> files, Directory localDir, Directory remoteDir, TransferFileCallback callback) throws IOException {
        ctChannel.writeShort(ControllerIdentifiers.REQUEST_SEND);
        ctChannel.writeInt(files.size());
        for (RemoteFile file : files) {
            ctChannel.writeUTF(file.getPath());
        }
        ctChannel.writeUTF(localDir.path);
        ctChannel.writeInt(localDir.fileSystem);
        ctChannel.writeUTF(remoteDir.path);
        receiveFiles(callback);
    }

    public List<RemoteFile> listLocalFiles(String path) throws RemoteException {
        return listLocalFiles(ioService, path);
    }

    public boolean deleteLocalFile(String file) throws RemoteException {
        return ioService.deleteFile(file);
    }

    public boolean createLocalDir(String parent, String child) throws RemoteException {
        return ioService.appendAndMkdirs(parent, child);
    }

    public List<RemoteFile> listClientFiles(String path) throws IOException {
        ctChannel.writeShort(ControllerIdentifiers.LIST_FILES);
        ctChannel.writeUTF(path);
        int listSize = ctChannel.readInt();
        if (listSize == -1) {
            return null;
        }
        ArrayList<RemoteFile> remoteFiles = new ArrayList<>(listSize);
        //| name       | path       | lastModified | size    | isDirectory |
        //| ---------- | ---------- | ------------ | ------- | ----------- |
        //| String:UTF | String:UTF | long:8b      | long:8b | boolean     |
        for (int i = 0; i < listSize; i++) {
            RemoteFile remoteFile = new RemoteFile(
                    ctChannel.readUTF(),//name
                    ctChannel.readUTF(),//path
                    ctChannel.readLong(),//lastModified
                    ctChannel.readLong(),//size
                    ctChannel.readBoolean()//isDirectory
            );
            remoteFiles.add(remoteFile);
        }
        return remoteFiles;
    }

    public boolean deleteRemoteFile(String file) throws IOException {
        ctChannel.writeShort(ControllerIdentifiers.DELETE_FILE);
        ctChannel.writeUTF(file);
        return ctChannel.readBoolean();
    }

    public boolean createRemoteDir(String parent, String child) throws IOException {
        ctChannel.writeShort(ControllerIdentifiers.MKDIR);
        ctChannel.writeUTF(parent);
        ctChannel.writeUTF(child);
        return ctChannel.readBoolean();
    }

    public static List<RemoteFile> listLocalFiles(IIOService ioService, String path) throws RemoteException {
        int[] chunkIds = ioService.listFiles(path);
        if (chunkIds == null) {
            return null;
        }
        ArrayList<RemoteFile> remoteFiles = new ArrayList<>();
        for (int chunkId : chunkIds) {
            remoteFiles.addAll(ioService.getAndRemoveFileListSlice(chunkId));
        }
        return remoteFiles;
    }

    public List<String> getConnectionListINames() {
        List<String> names = new ArrayList<>(connections.size());
        for (TransferConnection connection : connections) {
            names.add(connection.iName);
        }
        return names;
    }

    public synchronized void disconnect(){
        if (closed) {
            return;
        }
        closed = true;
        driveModeRunning = false;
        //先关闭控制通道与所有传输通道，避免传输线程继续使用缓冲区
        if (ctChannel != null) {
            try {
                //通知断开连接
                ctChannel.writeShort(ControllerIdentifiers.SHUTDOWN);
            } catch (IOException ignored) {
            }
            try {
                ctChannel.close();
            } catch (IOException ignored) {
            }
        }
        if (serverSocketChannel != null) {
            try {
                serverSocketChannel.close();
            } catch (IOException ignored) {
            }
        }
        //关闭所有用于传输的连接
        if (connections != null) {
            for (TransferConnection connection : connections) {
                try {
                    connection.close();
                } catch (IOException ignored) {
                }
            }
        }
        //通道关闭后再释放缓冲区块内存；若传输线程仍在收尾则延迟释放
        freeBuffersWhenIdle();
    }

    public synchronized void close(){
        if (closed) {
            return;
        }
        closed = true;
        driveModeRunning = false;
        //释放并清理缓冲区块；若传输线程仍在收尾则延迟释放
        freeBuffersWhenIdle();
        //关闭serverSocket，释放端口绑定，不用管其他accept的Socket
        if (serverSocketChannel != null) {
            try {
                serverSocketChannel.close();
            } catch (IOException e) {
                e.printStackTrace();
            }
        }
    }

    /**
     * 传输线程仍在使用缓冲区时，等待其结束后再释放直接内存。
     */
    private void freeBuffersWhenIdle() {
        if (!hasActiveTransfers()) {
            freeBuffersNative();
            return;
        }
        Thread releaseThread = new Thread(() -> {
            for (int i = 0; i < 100 && hasActiveTransfers(); i++) {
                try {
                    Thread.sleep(50);
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    break;
                }
            }
            freeBuffersNative();
        }, "ServerBufferRelease");
        releaseThread.setDaemon(true);
        releaseThread.start();
    }

    private void freeBuffersNative() {
        synchronized (buffers) {
            for (ByteBuffer buffer : buffers) {
                NativeMemory.freeBuffer(buffer);
            }
            buffers.clear();
        }
    }

    @Override
    protected WriteFileCall createWriteFileCall(LinkedBlockingDeque<ByteBuffer> buffers, int dequeCount) {
        return new DroidWriteFileCall(buffers,dequeCount,ioService);
    }

    @Override
    protected ReadFileCall createReadFileCall(LinkedBlockingDeque<ByteBuffer> buffers, List<RemoteFile> files, Directory localDir, Directory remoteDir, int operateThreadCount) {
        return new DroidReadFileCall(ioService,buffers,files,localDir,remoteDir,operateThreadCount);
    }
}
