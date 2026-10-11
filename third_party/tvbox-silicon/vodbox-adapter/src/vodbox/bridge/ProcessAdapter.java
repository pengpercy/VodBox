// SPDX-License-Identifier: GPL-3.0-only
package vodbox.bridge;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import org.json.JSONObject;
import tvbox.runtime.NativeProbe;
import tvbox.runtime.PluginSession;

/** Only adapts VodBox's IPC to the upstream runtime; plugin execution belongs to upstream. */
public final class ProcessAdapter {
    private static void write(Path target, JSONObject value) throws IOException {
        Files.createDirectories(target.getParent());
        Path temporary = target.resolveSibling(target.getFileName() + ".tmp");
        Files.write(temporary, value.toString().getBytes(StandardCharsets.UTF_8));
        Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
    }
    private static void convert(JSONObject input) throws Exception {
        JSONObject result;
        try {
            // Delegate the conversion and all bytecode repairs to the pinned upstream implementation.
            var method = dalvik.system.DexClassLoader.class.getDeclaredMethod("convert", String.class, String.class);
            method.setAccessible(true);
            Path output = Path.of(input.getString("output"));
            Files.createDirectories(output.toAbsolutePath().getParent());
            java.net.URL artifact = (java.net.URL) method.invoke(null, input.getString("input"), output.toAbsolutePath().getParent().toString());
            Files.copy(Path.of(artifact.toURI()), output, StandardCopyOption.REPLACE_EXISTING);
            int count;
            try (var jar = new java.util.jar.JarFile(output.toFile())) {
                count = (int) jar.stream().filter(e -> e.getName().endsWith(".class")).count();
            }
            int dexFiles = 1;
            String version = "";
            Path original = Path.of(input.getString("input"));
            try (var zip = new java.util.zip.ZipFile(original.toFile())) {
                var dex = zip.stream().filter(e -> e.getName().matches("classes[0-9]*\\.dex")).toList();
                dexFiles = dex.size();
                if (!dex.isEmpty()) try (var stream = zip.getInputStream(dex.get(0))) {
                    byte[] header = stream.readNBytes(8);
                    if (header.length == 8) version = new String(header, 4, 3, StandardCharsets.US_ASCII);
                }
            } catch (java.util.zip.ZipException rawDex) {
                try (var stream = Files.newInputStream(original)) {
                    byte[] header = stream.readNBytes(8);
                    if (header.length == 8) version = new String(header, 4, 3, StandardCharsets.US_ASCII);
                }
            }
            result = new JSONObject().put("ok", true).put("dexFiles", dexFiles).put("dexVersion", version).put("classCount", count);
        } catch (Throwable failure) {
            if (failure instanceof java.lang.reflect.InvocationTargetException && failure.getCause() != null) failure = failure.getCause();
            result = NativeProbe.failure(failure).put("ok", false);
        }
        write(Path.of(input.getString("result")), result);
    }

    private static JSONObject document(String path) throws Exception {
        String text = Files.readString(Path.of(path));
        if (!text.isEmpty() && text.charAt(0) == '\ufeff') text = text.substring(1);
        return new JSONObject(text);
    }
    public static void main(String[] args) throws Exception {
        if (args.length == 2 && "--convert".equals(args[0])) {
            System.setOut(System.err);
            convert(document(args[1]));
            return;
        }
        if (args.length != 2 || !"--serve".equals(args[0]))
            throw new IllegalArgumentException("Expected --serve request.json");
        System.setOut(System.err);
        java.security.Security.addProvider(new org.bouncycastle.jce.provider.BouncyCastleProvider());
        JSONObject input = document(args[1]);
        Path cache = Path.of(input.getString("cache"));
        PluginSession session = null;
        int exit = 0;
        try {
            session = NativeProbe.initialize(input);
            write(cache.resolve("ready.json"), new JSONObject().put("ready", true));
            BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
            String line;
            while ((line = reader.readLine()) != null) {
                JSONObject command = new JSONObject(line);
                String id = command.getString("id");
                if (!id.matches("[A-Za-z0-9-]{1,80}")) throw new IllegalArgumentException("Invalid request id");
                JSONObject result;
                try { result = new JSONObject().put("result", session.request(command.getJSONObject("params"))); }
                catch (Throwable failure) { result = NativeProbe.failure(failure); }
                write(cache.resolve("responses").resolve(id + ".json"), result);
            }
        } catch (Throwable failure) {
            exit = 1;
            write(cache.resolve("ready.json"), NativeProbe.failure(failure));
        } finally {
            if (session != null) try { session.close(); } catch (Throwable failure) { failure.printStackTrace(System.err); }
        }
        // Upstream plugins may own non-daemon threads after stdin is closed.
        Runtime.getRuntime().halt(exit);
    }
}
