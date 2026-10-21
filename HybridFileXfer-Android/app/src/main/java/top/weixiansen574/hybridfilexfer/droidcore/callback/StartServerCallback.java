package top.weixiansen574.hybridfilexfer.droidcore.callback;

import top.weixiansen574.async.BackstageTask;

public interface StartServerCallback extends BackstageTask.BaseEventHandler {
    void onBindFailed(int port);
    void onStatedServer();
    void onAccepted(String name);
    void onAcceptFailed(String name);
    void onPcOOM();
    void onMeOOM(int created, int localBufferCount);
    void onConnectSuccess();

    /**
     * v3.1：PC 以驱动模式连接成功，手机端应禁用本地文件管理入口。
     */
    default void onPcDriveModeStarted() {
    }

    /**
     * v3.1：PC 驱动模式结束。
     *
     * @param reason 结束原因，正常断开时可为 null
     */
    default void onPcDriveModeStopped(String reason) {
    }

    /**
     * v3.1：客户端请求 PC 控制模式，但手机端未允许。
     */
    default void onPcDriveModeRejected() {
    }
}
