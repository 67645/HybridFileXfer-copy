package top.weixiansen574.hybridfilexfer.engine;

import java.io.File;

/**
 * 启动 Mock 服务端，供 GUI 端到端联调使用。
 * 输出 PORT=<端口> 后阻塞，收到客户端 SHUTDOWN 后退出。
 */
public final class MockServerMain {
    private MockServerMain() {
    }

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("usage: MockServerMain <root>");
            System.exit(2);
        }
        File root = new File(args[0]);
        if (!root.isDirectory()) {
            System.err.println("root not found: " + root);
            System.exit(2);
        }

        MockHFXServer server = new MockHFXServer(root);
        System.out.println("PORT=" + server.getPort());
        System.out.flush();
        server.start();
        if (server.getError() != null) {
            server.getError().printStackTrace();
            System.exit(1);
        }
        System.out.println("MOCK_STOPPED");
    }
}
