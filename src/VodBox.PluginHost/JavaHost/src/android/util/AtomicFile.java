package android.util;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;

/**
 * Android {@code AtomicFile} 最小子集（VodBox 自有实现，语义与 Android 一致）。
 *
 * 语义：写入先落到 {@code <base>.new}，提交时把旧文件改名为 {@code <base>.bak}、再把 {@code .new}
 * 改名覆盖 base、最后删除备份。{@link #openRead()} 若发现遗留备份，会先恢复它。
 * 这样进程在写入中途崩溃也不会留下半截文件——读方要么见到旧内容、要么见到新内容。
 */
public class AtomicFile {

    private final File baseName;
    private final File newName;
    private final File backupName;

    public AtomicFile(File baseName) {
        this(baseName, baseName.getPath() + ".bak");
    }

    public AtomicFile(File baseName, String backupName) {
        this.baseName = baseName;
        this.newName = new File(baseName.getPath() + ".new");
        this.backupName = new File(backupName);
    }

    public File getBaseFile() {
        return baseName;
    }

    public boolean exists() {
        return baseName.exists() || newName.exists() || backupName.exists();
    }

    public void delete() {
        baseName.delete();
        newName.delete();
        backupName.delete();
    }

    /** 开始一次写：把现有 base 移到备份，然后返回指向 {@code .new} 的输出流。 */
    public FileOutputStream startWrite() throws IOException {
        if (baseName.exists()) {
            if (!backupName.exists()) {
                if (!baseName.renameTo(backupName)) {
                    System.err.println("jarhost: AtomicFile 无法备份 " + baseName + " -> " + backupName);
                }
            } else {
                baseName.delete();
            }
        }
        try {
            return new FileOutputStream(newName);
        } catch (FileNotFoundException first) {
            File parent = newName.getParentFile();
            if (parent != null && !parent.mkdirs() && !parent.isDirectory()) {
                throw new IOException("无法创建目录：" + parent, first);
            }
            try {
                return new FileOutputStream(newName);
            } catch (FileNotFoundException second) {
                throw new IOException("无法创建 " + newName, second);
            }
        }
    }

    /** 提交：把 {@code .new} 改名覆盖 base，删除备份。 */
    public void finishWrite(FileOutputStream stream) {
        if (stream == null) return;
        try {
            sync(stream);
            stream.close();
            if (!newName.renameTo(baseName)) {
                System.err.println("jarhost: AtomicFile 无法提交 " + newName + " -> " + baseName);
            }
            backupName.delete();
        } catch (IOException error) {
            failWrite(stream);
        }
    }

    /** 放弃本次写并尝试回滚到备份。 */
    public void failWrite(FileOutputStream stream) {
        if (stream == null) return;
        try {
            sync(stream);
            stream.close();
            newName.delete();
            baseName.delete();
            if (!backupName.renameTo(baseName)) {
                System.err.println("jarhost: AtomicFile 无法回滚 " + backupName + " -> " + baseName);
            }
        } catch (IOException error) {
            System.err.println("jarhost: AtomicFile 回滚失败：" + error);
        }
    }

    public FileInputStream openRead() throws FileNotFoundException {
        if (backupName.exists()) {
            baseName.delete();
            backupName.renameTo(baseName);
        }
        return new FileInputStream(baseName);
    }

    public byte[] readFully() throws IOException {
        try (FileInputStream stream = openRead()) {
            int available = stream.available();
            byte[] data = new byte[available];
            int position = 0;
            while (position < data.length) {
                int read = stream.read(data, position, data.length - position);
                if (read <= 0) break;
                position += read;
            }
            if (position == data.length) return data;
            byte[] trimmed = new byte[position];
            System.arraycopy(data, 0, trimmed, 0, position);
            return trimmed;
        }
    }

    private static void sync(FileOutputStream stream) {
        try {
            stream.getFD().sync();
        } catch (IOException ignored) {
            // 尽力而为；同步失败不阻断提交。
        }
    }
}
