package top.weixiansen574.hybridfilexfer.core.bean;

/**
 * Windows 目标路径兼容性测试。
 */
public final class DirectoryPathTest {
    private DirectoryPathTest() {
    }

    public static void main(String[] args) {
        Directory local = new Directory("/sdcard", Directory.FILE_SYSTEM_UNIX);
        Directory remote = new Directory("C:\\pics", Directory.FILE_SYSTEM_WINDOWS);

        assertEquals("C:\\pics\\normal.txt",
                local.generateTransferPath("/sdcard/normal.txt", remote));
        assertEquals("C:\\pics\\2025-2-8 17_10_25.jpg",
                local.generateTransferPath("/sdcard/2025-2-8 17:10:25.jpg", remote));
        assertEquals("C:\\pics\\_CON.txt",
                local.generateTransferPath("/sdcard/CON.txt", remote));
        assertEquals("C:\\pics\\_COM1.log",
                local.generateTransferPath("/sdcard/COM1.log", remote));
        assertEquals("C:\\pics\\trailing",
                local.generateTransferPath("/sdcard/trailing.", remote));
        assertEquals("C:\\pics\\trailing",
                local.generateTransferPath("/sdcard/trailing ", remote));
        assertEquals("C:\\pics\\_",
                local.generateTransferPath("/sdcard/..", remote));
        assertEquals("C:\\pics\\sub\\_AUX",
                local.generateTransferPath("/sdcard/sub/AUX", remote));

        Directory linuxRemote = new Directory("/mnt/ssd", Directory.FILE_SYSTEM_UNIX);
        assertEquals("/mnt/ssd/2025-2-8 17_10_25.jpg",
                local.generateTransferPath("/sdcard/2025-2-8 17:10:25.jpg", linuxRemote));
        assertEquals("/mnt/ssd/trailing.",
                local.generateTransferPath("/sdcard/trailing.", linuxRemote));

        System.out.println("DirectoryPathTest PASS");
    }

    private static void assertEquals(String expected, String actual) {
        if (!expected.equals(actual)) {
            throw new AssertionError("expected <" + expected + "> but was <" + actual + ">");
        }
    }
}
