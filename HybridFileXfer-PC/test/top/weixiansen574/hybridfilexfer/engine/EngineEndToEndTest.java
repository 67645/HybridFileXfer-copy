package top.weixiansen574.hybridfilexfer.engine;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

import top.weixiansen574.hybridfilexfer.Main;

/**
 * P1 端到端验证：
 * MockHFXServer 模拟 Android 服务端，通过 Main --engine 入口运行，
 * 经真实 TCP 控制通道/传输通道完成连接、列目录、下载、新建、上传、删除。
 */
public final class EngineEndToEndTest {
    private EngineEndToEndTest() {
    }

    public static void main(String[] args) throws Exception {
        Path base = Files.createTempDirectory("hfx-e2e-");
        Path mockRoot = Files.createDirectories(base.resolve("mock"));
        Path mockSource = Files.createDirectories(mockRoot.resolve("source"));
        Path pcRoot = Files.createDirectories(base.resolve("pc"));
        Path pcRecv = pcRoot.resolve("recv");

        Path hello = mockSource.resolve("hello.txt");
        Path empty = mockSource.resolve("empty.bin");
        Path chinese = mockSource.resolve("中文.txt");
        Path oneMb = mockSource.resolve("1mb.bin");
        Path mockDir = Files.createDirectories(mockSource.resolve("subdir"));
        Path nested = mockDir.resolve("nested.txt");
        Files.write(hello, "hello".getBytes(StandardCharsets.UTF_8));
        Files.write(empty, new byte[0]);
        Files.write(chinese, "你好，多轨快传".getBytes(StandardCharsets.UTF_8));
        Files.write(nested, "nested".getBytes(StandardCharsets.UTF_8));
        byte[] mb = new byte[1024 * 1024];
        for (int i = 0; i < mb.length; i++) {
            mb[i] = (byte) (i & 0xFF);
        }
        Files.write(oneMb, mb);

        MockHFXServer mockServer = new MockHFXServer(mockRoot.toFile());
        Thread mockThread = new Thread(mockServer::start, "mock-server");
        mockThread.start();

        String commands = String.join("\n",
                command("{\"id\":1,\"cmd\":\"connect\",\"mode\":\"ip\",\"address\":\"127.0.0.1\""
                        + ",\"port\":" + mockServer.getPort()
                        + ",\"homeDir\":" + Json.quote(jsonPath(pcRoot))
                        + ",\"driveMode\":1}"),
                command("{\"id\":2,\"cmd\":\"list\",\"path\":" + Json.quote(jsonPath(mockSource)) + "}"),
                command("{\"id\":3,\"cmd\":\"download\",\"paths\":["
                        + Json.quote(jsonPath(hello)) + ","
                        + Json.quote(jsonPath(empty)) + ","
                        + Json.quote(jsonPath(chinese)) + ","
                        + Json.quote(jsonPath(oneMb)) + ","
                        + Json.quote(jsonPath(mockDir)) + "]"
                        + ",\"sourceDir\":" + Json.quote(jsonPath(mockSource))
                        + ",\"targetDir\":" + Json.quote(jsonPath(pcRecv.resolve("sub"))) + "}"),
                command("{\"id\":4,\"cmd\":\"mkdir\",\"parent\":" + Json.quote(jsonPath(mockRoot))
                        + ",\"child\":\"newdir\"}"),
                command("{\"id\":5,\"cmd\":\"upload\",\"paths\":["
                        + Json.quote(jsonPath(pcRecv.resolve("sub").resolve("hello.txt"))) + ","
                        + Json.quote(jsonPath(pcRecv.resolve("sub").resolve("subdir"))) + "]"
                        + ",\"sourceDir\":" + Json.quote(jsonPath(pcRecv.resolve("sub")))
                        + ",\"targetDir\":" + Json.quote(jsonPath(mockRoot.resolve("upload"))) + "}"),
                command("{\"id\":6,\"cmd\":\"delete\",\"path\":"
                        + Json.quote(jsonPath(mockRoot.resolve("newdir"))) + "}"),
                command("{\"id\":7,\"cmd\":\"disconnect\"}"),
                command("{\"id\":8,\"cmd\":\"shutdown\"}")
        );

        ByteArrayOutputStream outputBytes = new ByteArrayOutputStream();
        InputStream originalIn = System.in;
        PrintStream originalOut = System.out;
        try {
            System.setIn(new ByteArrayInputStream(commands.getBytes(StandardCharsets.UTF_8)));
            System.setOut(new PrintStream(outputBytes, true, "UTF-8"));
            Main.main(new String[]{"--engine"});
            mockThread.join(5000);
            assertTrue(!mockThread.isAlive(), "mock server thread did not stop");
            String events = new String(outputBytes.toByteArray(), StandardCharsets.UTF_8);
            Path eventsLog = Paths.get(System.getProperty("java.io.tmpdir"), "hfx-e2e-events.log");
            Files.write(eventsLog, events.getBytes(StandardCharsets.UTF_8));
            System.setIn(originalIn);
            System.setOut(originalOut);
            System.out.println("P1 E2E events written to: " + eventsLog);

            if (mockServer.getError() != null) {
                throw new AssertionError("mock server error: " + mockServer.getError(), mockServer.getError());
            }
            assertTrue(events.contains("\"event\":\"connected\""), "missing connected event");
            assertTrue(events.contains("\"event\":\"files\""), "missing files event");
            assertTrue(events.contains("hello.txt") && events.contains("中文.txt"), "missing entries");
            assertTrue(events.contains("\"id\":3,\"success\":true"), "download result missing");
            assertTrue(events.contains("\"id\":4,\"success\":true"), "mkdir failed");
            assertTrue(events.contains("\"id\":5,\"success\":true"), "upload result missing");
            assertTrue(events.contains("\"id\":6,\"success\":true"), "delete failed");
            assertTrue(events.contains("\"event\":\"complete\""), "missing complete event");
            assertTrue(!events.contains("\"event\":\"error\""), "unexpected error event");

            Path downloadedHello = pcRecv.resolve("sub").resolve("hello.txt");
            Path downloadedEmpty = pcRecv.resolve("sub").resolve("empty.bin");
            Path downloadedChinese = pcRecv.resolve("sub").resolve("中文.txt");
            Path downloadedOneMb = pcRecv.resolve("sub").resolve("1mb.bin");
            assertTrue(Files.exists(downloadedHello), "hello.txt not downloaded");
            assertTrue("hello".equals(new String(Files.readAllBytes(downloadedHello), StandardCharsets.UTF_8)),
                    "hello.txt content mismatch");
            assertTrue(Files.size(downloadedEmpty) == 0, "empty.bin size mismatch");
            assertTrue("你好，多轨快传".equals(new String(Files.readAllBytes(downloadedChinese), StandardCharsets.UTF_8)),
                    "中文.txt content mismatch");
            assertTrue(Files.size(downloadedOneMb) == mb.length, "1mb.bin size mismatch");
            Path downloadedNested = pcRecv.resolve("sub").resolve("subdir").resolve("nested.txt");
            assertTrue(Files.exists(downloadedNested), "nested file not downloaded");
            assertTrue("nested".equals(new String(Files.readAllBytes(downloadedNested), StandardCharsets.UTF_8)),
                    "nested.txt content mismatch");
            assertTrue(!Files.exists(mockRoot.resolve("newdir")), "newdir not deleted");

            Path uploadedHello = mockRoot.resolve("upload").resolve("hello.txt");
            assertTrue(Files.exists(uploadedHello), "uploaded hello.txt missing");
            assertTrue("hello".equals(new String(Files.readAllBytes(uploadedHello), StandardCharsets.UTF_8)),
                    "uploaded hello.txt content mismatch");
            Path uploadedNested = mockRoot.resolve("upload").resolve("subdir").resolve("nested.txt");
            assertTrue(Files.exists(uploadedNested), "uploaded nested.txt missing");
            assertTrue("nested".equals(new String(Files.readAllBytes(uploadedNested), StandardCharsets.UTF_8)),
                    "uploaded nested.txt content mismatch");

            System.out.println("P1 E2E PASS (UTF-8 event log: " + eventsLog + ")");
            mockServer.stop();
            deleteRecursively(base);
        } catch (Throwable e) {
            System.setIn(originalIn);
            System.setOut(originalOut);
            System.err.println("P1 E2E FAIL, temp dir kept: " + base);
            e.printStackTrace();
            mockServer.stop();
            System.exit(1);
        }
    }

    private static String command(String json) {
        return json;
    }

    private static String jsonPath(Path path) {
        return path.toAbsolutePath().toString().replace('\\', '/');
    }

    private static void assertTrue(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError(message);
        }
    }

    private static void deleteRecursively(Path path) throws IOException {
        if (!Files.exists(path)) {
            return;
        }
        List<Path> paths = new ArrayList<>();
        Files.walk(path).forEach(paths::add);
        paths.sort(Comparator.reverseOrder());
        for (Path item : paths) {
            Files.deleteIfExists(item);
        }
    }
}
