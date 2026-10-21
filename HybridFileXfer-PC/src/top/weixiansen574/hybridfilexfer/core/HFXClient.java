package top.weixiansen574.hybridfilexfer.core;

import java.io.File;
import java.io.IOException;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.channels.SocketChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.locks.ReentrantLock;

import top.weixiansen574.hybridfilexfer.core.bean.Directory;
import top.weixiansen574.hybridfilexfer.core.bean.RemoteFile;
import top.weixiansen574.hybridfilexfer.core.callback.ClientCallBack;
import top.weixiansen574.hybridfilexfer.core.callback.ConnectServerCallback;
import top.weixiansen574.hybridfilexfer.core.callback.TransferFileCallback;
import top.weixiansen574.nio.DataByteChannel;

public abstract class HFXClient extends HFXService {

    protected final String serverControllerAddress;
    protected final int serverPort;
    protected final String homeDir;
    protected final int driveMode;
    protected boolean isRun = true;
    protected ClientCallBack callBack;
    protected int serverFileSystem = Directory.FILE_SYSTEM_UNIX;
    protected String serverHomeDir = "/";
    private final ReentrantLock controlLock = new ReentrantLock(true);

    public HFXClient(String serverControllerAddress, int serverPort,String homeDir) {
        this(serverControllerAddress, serverPort, homeDir, DRIVE_MODE_PHONE);
    }

    public HFXClient(String serverControllerAddress, int serverPort, String homeDir, int driveMode) {
        this.serverControllerAddress = serverControllerAddress;
        this.serverPort = serverPort;
        this.homeDir = homeDir;
        this.driveMode = driveMode;
    }

    public boolean connect(ConnectServerCallback callback) throws IOException {
        try {
            //System.out.println("正在连接控制通道：" + serverControllerAddress);
            callback.onConnectingControlChannel(serverControllerAddress, serverPort);
            ctChannel = new DataByteChannel(SocketChannel
                    .open(new InetSocketAddress(serverControllerAddress,serverPort)));

            ctChannel.write(CLIENT_HEADER.getBytes(StandardCharsets.UTF_8));
            ctChannel.writeInt(VERSION_CODE);
            ctChannel.writeByte(driveMode);
            if (!ctChannel.readBoolean()) {
                //System.out.println("版本不一致，你的版本：" + VERSION_CODE + "，对方版本：" + ctChannel.readInt());
                callback.onVersionMismatch(VERSION_CODE, ctChannel.readInt());
                ctChannel.close();
                return false;
            }
            if (!ctChannel.readBoolean()) {
                //服务端未允许 PC 控制模式
                callback.onDriveModeRejected();
                ctChannel.close();
                return false;
            }
        } catch (IOException e) {
            //System.out.println("控制通道连接到手机失败，请检查手机的服务端是否启动？");
            callback.onConnectControlFailed();
            return false;
        }
        int ipCount = ctChannel.readInt();
        String[] names = new String[ipCount];
        InetAddress[] addresses = new InetAddress[ipCount];
        InetAddress[] bindAddresses = new InetAddress[ipCount];

        for (int i = 0; i < ipCount; i++) {
            String name = ctChannel.readUTF();
            byte[] address = new byte[ctChannel.readByte()];
            ctChannel.readFully(address);
            InetAddress inetAddress = InetAddress.getByAddress(address);
            byte l46 = ctChannel.readByte();
            InetAddress bindAddress = null;
            if (l46 != 0) {
                byte[] bAddress = new byte[l46];
                ctChannel.readFully(bAddress);
                bindAddress = InetAddress.getByAddress(bAddress);
            }
            names[i] = name;
            addresses[i] = inetAddress;
            bindAddresses[i] = bindAddress;
        }
        connections = new ArrayList<>(ipCount);
        for (int i = 0; i < ipCount; i++) {
            SocketChannel socketChannel;
            String name = names[i];
            InetAddress inetAddress = addresses[i];
            InetAddress bindAddress = bindAddresses[i];
            /*System.out.printf("正在连接 网卡名：%s 远程地址：%s 绑定地址：%s\n", name, inetAddress.getHostAddress(), bindAddress == null ?
                    "null" : bindAddress.getHostAddress());*/
            callback.onConnectingTransferChannel(name, inetAddress, bindAddress);
            /*if (name.equals("USB_ADB") && !serverControllerAddress.equals("127.0.0.1")) {
                System.err.println("错误：你在手机上选用了USB_ADB网卡，但没有使用ADB进行连接");
                ctChannel.writeBoolean(false);
                socket.close();
                return false;
            }*/
            try {
                if (bindAddress == null) {
                    socketChannel = SocketChannel.open(new InetSocketAddress(inetAddress, serverPort));
                } else {
                    socketChannel = SocketChannel.open();
                    socketChannel.bind(new InetSocketAddress(bindAddress, 0));
                    socketChannel.connect(new InetSocketAddress(inetAddress, serverPort));
                }
                connections.add(new TransferConnection(name, new DataByteChannel(socketChannel)));
            } catch (IOException e) {
                callback.onConnectTransferChannelFailed(name,inetAddress, e);
                ctChannel.writeBoolean(false);
                ctChannel.writeUTF(name);
                ctChannel.close();
                return false;
            }
            ctChannel.writeBoolean(true);
            ctChannel.writeUTF(name);
            ctChannel.readBoolean();
        }
        //初始化缓冲区块
        int bufferCount = ctChannel.readInt();
        for (int i = 0; i < bufferCount; i++) {
            ByteBuffer buffer = createBuffer(FileBlock.BLOCK_SIZE);
            if (buffer != null){
                buffers.add(buffer);
            } else {
                String arch = System.getProperty("os.arch");
                long availableMemoryMB = getAvailableMemoryMB();
                /*System.out.println("内存不足，创建缓冲区块失败！请尝试调小缓冲区块数（1MB每块）。成功创建" + i + "块，需要"
                        + bufferCount + "块。当前JVM最大内存：" + maxMemoryMB + "MB");
                if (arch != null && !arch.contains("64")) {
                    System.out.println("检测你正在使用32位Java，内存受限，建议使用64位Java");
                }*/
                freeBuffers();
                callback.onOOM(i, bufferCount, availableMemoryMB, arch);
                ctChannel.writeBoolean(false);
                return false;
            }
        }
        ctChannel.writeBoolean(true);
        if (!ctChannel.readBoolean()) {
            //System.out.println("连接失败，手机端内存不足，请调小缓存区块数");
            callback.onRemoteOOM();
            return false;
        }
        //返回文件系统信息给对方
        ctChannel.writeInt(Directory.getCurrentFileSystem());
        //返回主路径信息给对方
        ctChannel.writeUTF(homeDir);
        //v3.1：读取服务端的文件系统与主目录
        serverFileSystem = ctChannel.readInt();
        serverHomeDir = ctChannel.readUTF();
        //System.out.println("传输通道已全部连接完成");
        List<String> channelNames = new ArrayList<>(connections.size());
        for (TransferConnection connection : connections) {
            channelNames.add(connection.iName);
        }
        callback.onConnectSuccess(channelNames);
        return true;
    }

    public abstract ByteBuffer createBuffer(int size);

    public abstract long getAvailableMemoryMB();

    public void start(ClientCallBack transferFileCallback) throws Exception {
        if (driveMode == DRIVE_MODE_PC) {
            throw new IllegalStateException("PC drive mode does not use the passive loop");
        }
        this.callBack = transferFileCallback;
        //LOOP
        while (isRun) {
            short id = ctChannel.readShort();
            switch (id) {
                case ControllerIdentifiers.LIST_FILES:
                    handleListFiles();
                    break;
                case ControllerIdentifiers.DELETE_FILE:
                    handleDeleteFile();
                    break;
                case ControllerIdentifiers.MKDIR:
                    handleMkdir();
                    break;
                case ControllerIdentifiers.REQUEST_RECEIVE:
                    handleReceiveFiles();
                    break;
                case ControllerIdentifiers.REQUEST_SEND:
                    handleSendFiles();
                    break;
                case ControllerIdentifiers.SHUTDOWN:
                    handleShutdown();
                    break;
            }
        }
    }


    private void handleDeleteFile() throws Exception {
        ctChannel.writeBoolean(deleteLocalFile(ctChannel.readUTF()));
    }

    protected abstract boolean deleteLocalFile(String path) throws Exception;
    /*public boolean deleteLocalFile(String path) {
        File file = new File(path);
        if (!file.exists()) {
            System.out.println("文件或目录不存在: " + path);
            return false;
        }

        // 如果是目录，递归删除
        if (file.isDirectory()) {
            File[] files = file.listFiles();
            if (files != null) { // 检查是否为空
                for (File subFile : files) {
                    deleteLocalFile(subFile.getAbsolutePath());
                }
            }
        }

        // 删除文件或空目录
        return file.delete();
    }*/

    private void handleMkdir() throws Exception {
        String parent = ctChannel.readUTF();
        String child = ctChannel.readUTF();
        ctChannel.writeBoolean(mkdir(parent, child));
    }

    protected abstract boolean mkdir(String parent, String child) throws Exception;

    private void handleShutdown() {
        disconnect();
        callBack.onExit();
        //System.out.println("收到停止指令，客户端已正常关闭！");
    }

    /**
     * PC 驱动模式：通知服务端关闭本次会话，然后断开本地连接。
     */
    public void shutdownServer() {
        controlLock.lock();
        try {
            if (ctChannel != null && ctChannel.isOpen()) {
                ctChannel.writeShort(ControllerIdentifiers.SHUTDOWN);
            }
        } catch (IOException e) {
            e.printStackTrace();
        } finally {
            controlLock.unlock();
            disconnect();
        }
    }

    /**
     * 主动断开控制通道与所有传输通道。
     */
    public void disconnect() {
        isRun = false;
        if (ctChannel != null) {
            try {
                ctChannel.close();
            } catch (IOException e) {
                e.printStackTrace();
            }
        }
        if (connections != null) {
            for (TransferConnection transferConnection : connections) {
                try {
                    transferConnection.close();
                } catch (IOException e) {
                    e.printStackTrace();
                }
            }
            connections.clear();
        }
        freeBuffers();
    }

    private void handleListFiles() throws Exception {
        String path = ctChannel.readUTF();
        //System.out.println(path);
        if (!path.equals("/")) {
            List<RemoteFile> files = listFiles(path);
            if (files != null) {
                ctChannel.writeInt(files.size());
                for (RemoteFile file : files) {
                    writeRemoteFile(ctChannel, file);
                }
            } else {
                ctChannel.writeInt(-1);
            }
        } else {
            File[] roots = File.listRoots();
            //判断是否是Linux的目录结构，Windows的根目录是C:\\，而不是所有盘符，Linux的根目录是“/”没有盘符概念
            if (roots.length == 1 && roots[0].getAbsolutePath().equals("/")) {
                List<RemoteFile> files = listFiles(roots[0].getPath());
                if (files == null) {
                    ctChannel.writeInt(-1);
                    return;
                }
                ctChannel.writeInt(files.size());
                for (RemoteFile file : files) {
                    writeRemoteFile(ctChannel, file);
                }
            } else {//Windows的
                ctChannel.writeInt(roots.length);
                for (File file : roots) {
                    //不要getName，否则空白
                    writeRemoteFile(ctChannel, new RemoteFile(
                            file.getPath(), file.getPath(), file.lastModified(), file.length(), file.isDirectory()));
                }
            }
        }
        //已弃用ObjectOutputStream
        //| name       | path       | lastModified | size    | isDirectory |
        //| ---------- | ---------- | ------------ | ------- | ----------- |
        //| String:UTF | String:UTF | long:8b      | long:8b | boolean     |
    }

    protected abstract List<RemoteFile> listFiles(String path) throws Exception;

    private void handleReceiveFiles() throws IOException {
        //System.out.println("准备接收");
        callBack.onReceiving();
        isRun = receiveFiles(callBack);
    }

    private void handleSendFiles() throws IOException {
        int listSize = ctChannel.readInt();
        List<RemoteFile> fileList = new ArrayList<>(listSize);
        for (int i = 0; i < listSize; i++) {
            fileList.add(new RemoteFile(new File(ctChannel.readUTF())));
        }
        Directory remoteDir = readDirectory(ctChannel);//对方的localDir
        Directory localDir = new Directory(ctChannel.readUTF(), Directory.getCurrentFileSystem());//对方为remoteDir
        callBack.onSending();
        isRun = sendFiles(fileList,localDir,remoteDir,callBack);
    }

    /**
     * PC 驱动模式：列出手机端目录。
     * 返回 null 表示目录无法访问。
     */
    public List<RemoteFile> listServerFiles(String path) throws IOException {
        controlLock.lock();
        try {
            ctChannel.writeShort(ControllerIdentifiers.PC_LIST_FILES);
            ctChannel.writeUTF(path);
            int listSize = ctChannel.readInt();
            if (listSize == -1) {
                return null;
            }
            List<RemoteFile> files = new ArrayList<>(listSize);
            for (int i = 0; i < listSize; i++) {
                files.add(readRemoteFile(ctChannel));
            }
            return files;
        } finally {
            controlLock.unlock();
        }
    }

    /**
     * PC 驱动模式：删除手机端文件或目录。
     */
    public boolean deleteServerFile(String path) throws IOException {
        controlLock.lock();
        try {
            ctChannel.writeShort(ControllerIdentifiers.PC_DELETE_FILE);
            ctChannel.writeUTF(path);
            return ctChannel.readBoolean();
        } finally {
            controlLock.unlock();
        }
    }

    /**
     * PC 驱动模式：在手机端创建目录。
     */
    public boolean mkdirServer(String parent, String child) throws IOException {
        controlLock.lock();
        try {
            ctChannel.writeShort(ControllerIdentifiers.PC_MKDIR);
            ctChannel.writeUTF(parent);
            ctChannel.writeUTF(child);
            return ctChannel.readBoolean();
        } finally {
            controlLock.unlock();
        }
    }

    /**
     * PC 驱动模式：手机 -> PC。
     *
     * @param files     手机端文件列表
     * @param localDir  手机端源目录
     * @param remoteDir PC 端目标目录
     */
    public void downloadFiles(List<RemoteFile> files, Directory localDir, Directory remoteDir,
                              TransferFileCallback callback) throws IOException {
        controlLock.lock();
        try {
            ctChannel.writeShort(ControllerIdentifiers.PC_REQUEST_SEND);
            ctChannel.writeInt(files.size());
            for (RemoteFile file : files) {
                ctChannel.writeUTF(file.getPath());
            }
            writeDirectory(ctChannel, localDir);
            writeDirectory(ctChannel, remoteDir);
            receiveFiles(callback);
        } finally {
            controlLock.unlock();
        }
    }

    /**
     * PC 驱动模式：PC -> 手机。
     *
     * @param files     PC 端文件列表
     * @param localDir  PC 端源目录
     * @param remoteDir 手机端目标目录
     */
    public void uploadFiles(List<RemoteFile> files, Directory localDir, Directory remoteDir,
                            TransferFileCallback callback) throws IOException {
        controlLock.lock();
        try {
            ctChannel.writeShort(ControllerIdentifiers.PC_REQUEST_RECEIVE);
            writeDirectory(ctChannel, localDir);
            writeDirectory(ctChannel, remoteDir);
            sendFiles(files, localDir, remoteDir, callback);
        } finally {
            controlLock.unlock();
        }
    }

    public boolean isPcDriveMode() {
        return driveMode == DRIVE_MODE_PC;
    }

    public int getServerFileSystem() {
        return serverFileSystem;
    }

    public String getServerHomeDir() {
        return serverHomeDir;
    }

    public void freeBuffers(){
        buffers.clear();
    }

}
