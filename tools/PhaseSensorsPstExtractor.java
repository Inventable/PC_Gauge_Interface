import com.pff.PSTAttachment;
import com.pff.PSTException;
import com.pff.PSTFile;
import com.pff.PSTFolder;
import com.pff.PSTMessage;

import java.io.BufferedWriter;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.Vector;

public class PhaseSensorsPstExtractor {
    private final File outputDir;
    private final File messagesDir;
    private final File attachmentsDir;
    private final BufferedWriter indexWriter;
    private int exportedMessages = 0;

    public static void main(String[] args) throws Exception {
        if (args.length < 2) {
            System.err.println("Usage: PhaseSensorsPstExtractor <pst-path> <output-dir>");
            System.exit(1);
        }

        new PhaseSensorsPstExtractor(new File(args[1])).run(new File(args[0]));
    }

    private PhaseSensorsPstExtractor(File outputDir) throws IOException {
        this.outputDir = outputDir;
        this.messagesDir = new File(outputDir, "messages");
        this.attachmentsDir = new File(outputDir, "attachments");
        this.messagesDir.mkdirs();
        this.attachmentsDir.mkdirs();
        this.indexWriter = new BufferedWriter(new OutputStreamWriter(
            new FileOutputStream(new File(outputDir, "messages-index.tsv")),
            StandardCharsets.UTF_8));
        this.indexWriter.write("index\tdescriptorId\tfolder\tsubject\tsenderName\tsenderEmail\tsent\treceived\ttextPath\tattachments\n");
    }

    private void run(File pstPath) throws Exception {
        try {
            PSTFile pstFile = new PSTFile(pstPath.getAbsolutePath());
            processFolder(pstFile.getRootFolder(), "");
        } finally {
            this.indexWriter.close();
        }

        System.out.println("Exported " + this.exportedMessages + " message(s) to " + this.outputDir.getPath());
    }

    private void processFolder(PSTFolder folder, String parentPath) throws PSTException, IOException {
        String displayName = safe(folder.getDisplayName());
        String folderPath = parentPath.length() == 0 ? displayName : parentPath + "\\" + displayName;

        if (folder.hasSubfolders()) {
            Vector<PSTFolder> childFolders = folder.getSubFolders();
            for (PSTFolder childFolder : childFolders) {
                processFolder(childFolder, folderPath);
            }
        }

        if (folder.getContentCount() <= 0) {
            return;
        }

        PSTMessage email = (PSTMessage) folder.getNextChild();
        while (email != null) {
            if (isRelevant(folderPath, email)) {
                exportMessage(folderPath, email);
            }

            email = (PSTMessage) folder.getNextChild();
        }
    }

    private boolean isRelevant(String folderPath, PSTMessage email) {
        String haystack = (
            folderPath + " " +
            safe(email.getSubject()) + " " +
            safe(email.getSenderName()) + " " +
            safe(email.getSenderEmailAddress()) + " " +
            safe(email.getDisplayTo()) + " " +
            safe(email.getDisplayCC())
        ).toLowerCase(Locale.ROOT);

        return haystack.contains("phase_sensors")
            || haystack.contains("phasesensors.com")
            || haystack.contains("phase sensors")
            || haystack.contains("temp polynomial")
            || haystack.contains("raw counts")
            || haystack.contains("binary format")
            || haystack.contains("transducer flash")
            || haystack.contains("measurement storage")
            || haystack.contains("passthru")
            || haystack.contains("pass-thru")
            || haystack.contains("filesystem");
    }

    private void exportMessage(String folderPath, PSTMessage email) throws IOException {
        this.exportedMessages++;
        long descriptorId = email.getDescriptorNodeId();
        String subject = safe(email.getSubject());
        String fileBase = String.format(Locale.ROOT, "%04d - %d - %s", this.exportedMessages, descriptorId, sanitize(subject));
        File messageFile = new File(this.messagesDir, fileBase + ".txt");
        StringBuilder attachmentSummary = new StringBuilder();

        try {
            int attachmentCount = email.getNumberOfAttachments();
            for (int i = 0; i < attachmentCount; i++) {
                PSTAttachment attachment = email.getAttachment(i);
                String attachmentName = safe(attachment.getLongFilename());
                if (attachmentName.length() == 0) {
                    attachmentName = safe(attachment.getFilename());
                }
                if (attachmentName.length() == 0) {
                    attachmentName = "attachment-" + i;
                }

                if (attachmentSummary.length() > 0) {
                    attachmentSummary.append("; ");
                }
                attachmentSummary.append(attachmentName);

                if (shouldSaveAttachment(attachmentName)) {
                    saveAttachment(attachment, new File(this.attachmentsDir,
                        String.format(Locale.ROOT, "%04d - %s", this.exportedMessages, sanitize(attachmentName))));
                }
            }
        } catch (Exception ignored) {
            if (attachmentSummary.length() > 0) {
                attachmentSummary.append("; ");
            }
            attachmentSummary.append("[attachment read error]");
        }

        BufferedWriter writer = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(messageFile), StandardCharsets.UTF_8));
        try {
            writer.write("Subject: " + subject + "\n");
            writer.write("DescriptorId: " + descriptorId + "\n");
            writer.write("Folder: " + folderPath + "\n");
            writer.write("Sender: " + safe(email.getSenderName()) + " <" + safe(email.getSenderEmailAddress()) + ">\n");
            writer.write("To: " + safe(email.getDisplayTo()) + "\n");
            writer.write("CC: " + safe(email.getDisplayCC()) + "\n");
            writer.write("Sent: " + formatDate(email.getClientSubmitTime()) + "\n");
            writer.write("Attachments: " + attachmentSummary + "\n");
            writer.write("\n");
            writer.write(safe(email.getBody()));
            writer.write("\n");
        } finally {
            writer.close();
        }

        this.indexWriter.write(this.exportedMessages + "\t"
            + descriptorId + "\t"
            + tsv(folderPath) + "\t"
            + tsv(subject) + "\t"
            + tsv(email.getSenderName()) + "\t"
            + tsv(email.getSenderEmailAddress()) + "\t"
            + tsv(formatDate(email.getClientSubmitTime())) + "\t"
            + "\t"
            + tsv(messageFile.getPath()) + "\t"
            + tsv(attachmentSummary.toString()) + "\n");
    }

    private static boolean shouldSaveAttachment(String name) {
        String lower = name.toLowerCase(Locale.ROOT);
        return !(lower.endsWith(".png")
            || lower.endsWith(".jpg")
            || lower.endsWith(".jpeg")
            || lower.endsWith(".gif")
            || lower.endsWith(".bmp")
            || lower.endsWith(".tif")
            || lower.endsWith(".tiff")
            || lower.endsWith(".webp")
            || lower.endsWith(".ico"));
    }

    private static void saveAttachment(PSTAttachment attachment, File target) throws IOException, PSTException {
        InputStream input = attachment.getFileInputStream();
        FileOutputStream output = new FileOutputStream(target);
        try {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = input.read(buffer)) >= 0) {
                output.write(buffer, 0, read);
            }
        } finally {
            input.close();
            output.close();
        }
    }

    private static String formatDate(Date date) {
        if (date == null) {
            return "";
        }
        return new SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.ROOT).format(date);
    }

    private static String safe(String value) {
        return value == null ? "" : value;
    }

    private static String sanitize(String value) {
        String sanitized = safe(value).replaceAll("[\\\\/:*?\"<>|\\r\\n\\t]+", "_").replaceAll("\\s+", " ").trim();
        if (sanitized.length() == 0) {
            sanitized = "message";
        }
        if (sanitized.length() > 110) {
            sanitized = sanitized.substring(0, 110);
        }
        return sanitized;
    }

    private static String tsv(String value) {
        return safe(value).replace('\t', ' ').replace('\r', ' ').replace('\n', ' ');
    }
}
