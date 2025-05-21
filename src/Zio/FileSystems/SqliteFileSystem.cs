
namespace Zio.FileSystems.SQLVFS
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Zio;
    using Zio.FileSystems;
    using System.Runtime.InteropServices;
    using Microsoft.Data.Sqlite;
    using System.Text.RegularExpressions;
    using FFmpeg.AutoGen;

    public class SqliteFileSystem : IFileSystem, IDisposable
    {
        private readonly SqliteConnection _connection;
        //private readonly Regex _wildcardRegex = new Regex("([^\\[\\]\\(\\)\\\\\\?\\*])", RegexOptions.Compiled);

        public SqliteFileSystem(string databasePath)
        {
            SQLitePCL.Batteries_V2.Init();
            _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;");
            _connection.Open();
            CreateTableIfNotExists();
        }
        public const string DatabaseTableName = "FileEntries";
        private void CreateTableIfNotExists(string tableName = DatabaseTableName)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {tableName} (
                Path TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Content BLOB,
                Size INTEGER NOT NULL,
                CreationTime DATETIME NOT NULL,
                LastAccessTime DATETIME NOT NULL,
                LastWriteTime DATETIME NOT NULL,
                Attributes INTEGER NOT NULL,
                TargetPath TEXT
            );
        ";
            cmd.ExecuteNonQuery();
        }
        /// <summary>
        /// 检测path是否存在,如果不存在,会按序依次创建
        /// </summary>
        /// <param name="path">不带文件名的路径</param>
        private void CheckDirectory(string path)
        {
            //string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(path) || path.EndsWith(":") || path == "/" || DirectoryExists(path))
            {
                return;
            }
            else
            {
                string dir = Path.GetDirectoryName(path);
                CheckDirectory(dir);
                CreateDirectory(path);
                //Directory.CreateDirectory(path);
            }
        }
        public void CreateDirectory(UPath path)
        {

            var normalizedPath = NormalizePath(path);
            var invalidPathChars = System.IO.Path.GetInvalidPathChars();
            if (normalizedPath.IndexOfAny(invalidPathChars) != -1)
            {
                throw new Exception($"无效的文件目录：{path}");
            }
            if (DirectoryExists(normalizedPath)) return;

            //using var transaction = _connection.BeginTransaction();
            try
            {            
                //string firstDir = path.GetFirstDirectory(out var remainingPath);
                var entry = new SqliteFileEntry
                {
                    Path = normalizedPath,
                    Name = "",
                    EntryType = SqliteFileEntryType.Directory,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Attributes = FileAttributes.Directory,
                    Size = 0
                };

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
                INSERT INTO {DatabaseTableName} (Path, Name, Type, Size, CreationTime, LastAccessTime, LastWriteTime, Attributes)
                VALUES (@Path, @Name, @Type, @Size, @CreationTime, @LastAccessTime, @LastWriteTime, @Attributes)
            ";
                cmd.Parameters.AddWithValue("@Path", entry.Path);
                cmd.Parameters.AddWithValue("@Name", entry.Name);
                cmd.Parameters.AddWithValue("@Type", (int)entry.EntryType);
                cmd.Parameters.AddWithValue("@Size", entry.Size);
                cmd.Parameters.AddWithValue("@CreationTime", entry.CreationTime);
                cmd.Parameters.AddWithValue("@LastAccessTime", entry.LastAccessTime);
                cmd.Parameters.AddWithValue("@LastWriteTime", entry.LastWriteTime);
                cmd.Parameters.AddWithValue("@Attributes", (int)entry.Attributes);
                cmd.ExecuteNonQuery();

                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }

        public bool DirectoryExists(UPath path)
        {
            if (path.IsNull || path.IsEmpty || path.FullName == "/")
            {
                return true;
            }
            return Exists(path, SqliteFileEntryType.Directory);
        }
        private bool Exists(UPath path, SqliteFileEntryType? type = null)
        {
            var normalizedPath = NormalizePath(path);
            using var cmd = _connection.CreateCommand();
            string fileTypeContion = "";
            if (type.HasValue)
            {
                if (type == SqliteFileEntryType.Directory)
                {
                    fileTypeContion = "AND Type = 0";
                }
                else
                {
                    fileTypeContion = "AND Type > 0";
                    //fileTypeContion = "AND Type != @Type";
                }
            }
            cmd.CommandText = $@"
            SELECT COUNT(*) FROM {DatabaseTableName} 
            WHERE Path = @Path " + fileTypeContion;
            cmd.Parameters.AddWithValue("@Path", normalizedPath);
            //if (type.HasValue)
            //    cmd.Parameters.AddWithValue("@Type", (int)type.Value);
            var c = (long)cmd.ExecuteScalar();
            return c > 0;
        }

        public void MoveDirectory(UPath srcPath, UPath destPath)
        {
            var srcNormalized = NormalizePath(srcPath);
            var destNormalized = NormalizePath(destPath);
            // 检查源目录是否存在
            if (!DirectoryExists(srcNormalized))
                throw new DirectoryNotFoundException($"Source directory not found: {srcPath}");
            // 检查目标是否存在
            if (Exists(destNormalized))
                throw new IOException($"Destination {destPath} already exists");
            var invalidPathChars = Path.GetInvalidPathChars();
            if (destNormalized.IndexOfAny(invalidPathChars) != -1)
            {
                throw new Exception($"无效的文件目录：{destNormalized}");
            }
            using var transaction = _connection.BeginTransaction();
            try
            {
                // 重命名目录
                UpdatePath(srcNormalized, destNormalized);
                // 递归更新子项路径
                UpdateChildPaths(srcNormalized, destNormalized);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private void UpdatePath(string oldPath, string newPath)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
            UPDATE {DatabaseTableName} SET Path = @NewPath WHERE Path = @OldPath";
            cmd.Parameters.AddWithValue("@NewPath", newPath);
            //cmd.Parameters.AddWithValue("@NewName", Path.GetFileName(newPath));
            cmd.Parameters.AddWithValue("@OldPath", oldPath);
            cmd.ExecuteNonQuery();
        }

        private void UpdateChildPaths(string oldPrefix, string newPrefix)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
            UPDATE {DatabaseTableName} SET Path = replace(Path, @OldPrefix, @NewPrefix)
            WHERE Path LIKE @OldPrefix || '%' AND Path != @OldPrefix";
            cmd.Parameters.AddWithValue("@OldPrefix", oldPrefix);
            cmd.Parameters.AddWithValue("@NewPrefix", newPrefix);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 必须调用Stream.Flush()才能把数据流写入数据库
        /// </summary>
        /// <param name="path"></param>
        /// <param name="mode"></param>
        /// <param name="access"></param>
        /// <param name="share"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public Stream OpenFile(UPath path, FileMode mode, FileAccess access, FileShare share = FileShare.None)
        {
            var normalizedPath = NormalizePath(path);
            var invalidPathChars = System.IO.Path.GetInvalidPathChars();
            if (normalizedPath.IndexOfAny(invalidPathChars) != -1)
            {
                throw new Exception($"无效的文件目录：{path}");
            }
            var invalidFileChars = System.IO.Path.GetInvalidFileNameChars();
            if (Path.GetFileName(normalizedPath).IndexOfAny(invalidFileChars) != -1)
            {
                throw new Exception($"无效的文件名：{path}");
            }
            //using var transaction = _connection.BeginTransaction();
            try
            {
                switch (mode)
                {
                    case FileMode.CreateNew:
                        if (FileExists(normalizedPath))
                            throw new IOException("File already exists");
                        CreateFileEntry(normalizedPath);
                        break;
                    case FileMode.Create:
                        if (this.Exists(normalizedPath))
                        {
                            this.DeleteFile(normalizedPath);
                        }
                        CreateFileEntry(normalizedPath);
                        break;
                    case FileMode.OpenOrCreate:
                        if (!this.Exists(normalizedPath))
                        {
                            CreateFileEntry(normalizedPath);
                        }
                        break;
                    case FileMode.Open:
                        if (!FileExists(normalizedPath))
                            throw new FileNotFoundException("File not found", path.ToString());
                        break;
                        // 处理其他FileMode情况
                }

                var entry = GetFileEntry(normalizedPath);
                var stream = new SqliteFileMemoryStream(1024);
                if (entry.Content != null && entry.Content.Length > 0)
                {
                    stream.Write(entry.Content, 0, entry.Content.Length);
                    stream.Position = 0;
                }
                // 处理写入模式
                if (access == FileAccess.Write || access == FileAccess.ReadWrite)
                {
                    stream.OnWriteFlush += (sender) =>
                    {
                        entry.Content = ((MemoryStream)stream).ToArray();
                        entry.Size = entry.Content.Length;
                        entry.LastWriteTime = DateTime.Now;
                        UpdateFileEntry(entry);
                        //transaction.Commit();
                    };
                }

                return stream;
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }


        // 辅助方法：获取文件条目（带类型检查）

        private SqliteFileEntry GetFileEntry(string path)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {DatabaseTableName} WHERE Path = @Path";
            cmd.Parameters.AddWithValue("@Path", path);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new FileNotFoundException("File not found", path);

            return new SqliteFileEntry
            {
                Path = reader.GetString(0),
                Name = reader.GetString(1),
                EntryType = (SqliteFileEntryType)reader.GetInt32(2),
                Content = reader.GetValue(3) as byte[] ?? Array.Empty<byte>(),
                Size = reader.GetInt64(4),
                CreationTime = reader.GetDateTime(5),
                LastAccessTime = reader.GetDateTime(6),
                LastWriteTime = reader.GetDateTime(7),
                Attributes = (FileAttributes)reader.GetInt32(8),
                TargetPath = reader.IsDBNull(9) ? null : reader.GetString(9)
            };
            //if (entry.Type != FileSystemEntryType.File)
            //    throw new InvalidDataException($"路径 {path} 不是文件");
        }

        private void CreateFileEntry(string path)
        {
            CheckDirectory(Path.GetDirectoryName(path));
            var entry = new SqliteFileEntry
            {
                Path = path,
                Name = Path.GetFileName(path),
                EntryType = SqliteFileEntryType.File,
                CreationTime = DateTime.Now,
                LastAccessTime = DateTime.Now,
                LastWriteTime = DateTime.Now,
                Attributes = FileAttributes.Normal,
                Size = 0
            };

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
            INSERT INTO {DatabaseTableName} (Path, Name, Type, Size, CreationTime, LastAccessTime, LastWriteTime, Attributes)
            VALUES (@Path, @Name, @Type, @Size, @CreationTime, @LastAccessTime, @LastWriteTime, @Attributes)
        ";
            cmd.Parameters.AddWithValue("@Path", entry.Path);
            cmd.Parameters.AddWithValue("@Name", entry.Name);
            cmd.Parameters.AddWithValue("@Type", (int)entry.EntryType);
            cmd.Parameters.AddWithValue("@Size", entry.Size);
            cmd.Parameters.AddWithValue("@CreationTime", entry.CreationTime);
            cmd.Parameters.AddWithValue("@LastAccessTime", entry.LastAccessTime);
            cmd.Parameters.AddWithValue("@LastWriteTime", entry.LastWriteTime);
            cmd.Parameters.AddWithValue("@Attributes", (int)entry.Attributes);
            cmd.ExecuteNonQuery();
        }

        private void CreateFileEntry(SqliteFileEntry destEntry)
        {
            if (destEntry == null)
                throw new ArgumentNullException(nameof(destEntry));
            var destPath=new UPath(destEntry.Path);
            var normalizedPath = NormalizePath(destPath);
            destEntry.Path = normalizedPath; // 确保路径格式统一
            if (destEntry.EntryType == SqliteFileEntryType.Directory)
            {
                destEntry.Name = "";//destPath.GetDirectory();
            }
            
            // 插入目标文件到数据库
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
            INSERT INTO {DatabaseTableName} (
                Path, Name, Type, Content, Size, 
                CreationTime, LastAccessTime, LastWriteTime, Attributes
            ) VALUES (
                @Path, @Name, @Type, @Content, @Size, 
                @CreationTime, @LastAccessTime, @LastWriteTime, @Attributes
            )";
            //string firstDir = destEntry.Path.GetFirstDirectory(out var remainingPath);
            cmd.Parameters.AddWithValue("@Path", destEntry.Path);
            cmd.Parameters.AddWithValue("@Name", destEntry.Name);
            cmd.Parameters.AddWithValue("@Type", (int)destEntry.EntryType);
            cmd.Parameters.AddWithValue("@Content", destEntry.Content == null ? DBNull.Value : destEntry.Content);
            cmd.Parameters.AddWithValue("@Size", destEntry.Size);
            cmd.Parameters.AddWithValue("@CreationTime", destEntry.CreationTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("@LastAccessTime", destEntry.LastAccessTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("@LastWriteTime", destEntry.LastWriteTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("@Attributes", (int)destEntry.Attributes);

            //cmd.ExecuteNonQuery();
            try
            {
                CheckDirectory(normalizedPath);
                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new InvalidOperationException($"未找到要更新的文件项：{destEntry.Path}");
            }
            catch (SqliteException ex) //when (ex.SqliteErrorCode == (int)SqliteErrorConstraint.ConstraintViolation)
            {
                throw new ArgumentException($"创建文件失败：{destEntry.Path}", nameof(destEntry), ex);
            }
        }
        private void UpdateFileEntry(SqliteFileEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            var normalizedPath = NormalizePath(new UPath(entry.Path));
            entry.Path = normalizedPath; // 确保路径格式统一

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        UPDATE {DatabaseTableName} SET
            Name = @Name,
            Type = @Type,
            Content = @Content,
            Size = @Size,
            CreationTime = @CreationTime,
            LastAccessTime = @LastAccessTime,
            LastWriteTime = @LastWriteTime,
            Attributes = @Attributes,
            TargetPath = @TargetPath
        WHERE Path = @Path";

            // 绑定参数（处理可能的 null 值）
            cmd.Parameters.AddWithValue("@Name", entry.Name ?? string.Empty);
            cmd.Parameters.AddWithValue("@Type", (int)entry.EntryType);
            cmd.Parameters.AddWithValue("@Content", entry.Content == null ? DBNull.Value : entry.Content); // BLOB 字段处理 null
            cmd.Parameters.AddWithValue("@Size", entry.Size);
            cmd.Parameters.AddWithValue("@CreationTime", entry.CreationTime.ToUniversalTime()); // 存储 UTC 时间确保跨时区一致性
            cmd.Parameters.AddWithValue("@LastAccessTime", entry.LastAccessTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("@LastWriteTime", entry.LastWriteTime.ToUniversalTime());
            cmd.Parameters.AddWithValue("@Attributes", (int)entry.Attributes);

            // 符号链接目标路径处理（允许 null）
            if (entry.TargetPath == null)
                cmd.Parameters.AddWithValue("@TargetPath", DBNull.Value);
            else
                cmd.Parameters.AddWithValue("@TargetPath", NormalizePath(new UPath(entry.TargetPath))); // 标准化目标路径

            cmd.Parameters.AddWithValue("@Path", entry.Path);

            try
            {
                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new InvalidOperationException($"未找到要更新的文件项：{entry.Path}");
            }
            catch (SqliteException ex) //when (ex.SqliteErrorCode == (int)SqliteErrorConstraint.ConstraintViolation)
            {
                throw new ArgumentException($"更新文件失败：{entry.Path}", nameof(entry), ex);
            }
        }
        private string NormalizePath(UPath path) =>
            path.IsAbsolute ? path.ToString() : $"/{path}";

        // 实现IDisposable
        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }

        public void DeleteDirectory(UPath path, bool isRecursive)
        {
            var normalizedPath = NormalizePath(path); // 标准化路径（确保绝对路径格式）

            using var transaction = _connection.BeginTransaction();
            try
            {
                // 检查目录是否存在
                if (!DirectoryExists(normalizedPath))
                    throw new DirectoryNotFoundException($"目录不存在: {path}");

                if (!isRecursive)
                {
                    // 非递归模式：仅删除空目录
                    if (HasChildEntries(normalizedPath))
                        throw new IOException($"目录不为空: {path}");
                }

                // 构建删除条件
                var deleteCondition = isRecursive
                    ? $"Path = @Path OR Path LIKE @Path || '/%'"
                    : "Path = @Path";

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM {DatabaseTableName} WHERE {deleteCondition}";
                cmd.Parameters.AddWithValue("@Path", normalizedPath);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0 && isRecursive)
                    throw new InvalidOperationException($"未找到要删除的目录: {path}");

                transaction.Commit();
            }
            catch (SqliteException ex)// when (ex.SqliteErrorCode == (int)SqliteError.Constraint)
            {
                transaction.Rollback();
                throw new IOException("删除目录时发生约束冲突（可能包含受保护的文件）", ex);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private bool HasChildEntries(string directoryPath)
        {
            // 检查目录是否包含子项（非目录本身）
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        SELECT COUNT(*) FROM {DatabaseTableName} 
        WHERE Path LIKE @DirectoryPath || '/%' AND Path != @DirectoryPath";
            cmd.Parameters.AddWithValue("@DirectoryPath", directoryPath);
            return (long)cmd.ExecuteScalar() > 0;
        }
        public void CopyFile(UPath srcPath, UPath destPath, bool overwrite)
        {
            var srcNormalized = NormalizePath(srcPath);  // 标准化源路径
            var destNormalized = NormalizePath(destPath);  // 标准化目标路径
            var invalidPathChars = System.IO.Path.GetInvalidPathChars();
            if (destNormalized.IndexOfAny(invalidPathChars) != -1)
            {
                throw new Exception($"无效的文件目录：{destNormalized}");
            }
            var invalidFileChars = System.IO.Path.GetInvalidFileNameChars();
            if (Path.GetFileName(destNormalized).IndexOfAny(invalidFileChars) != -1)
            {
                throw new Exception($"无效的文件名：{destNormalized}");
            }
            // 验证源文件是否存在且为文件类型
            // 获取源文件信息
            var srcEntry = GetFileEntry(srcNormalized);
            if (!FileExists(srcNormalized) ||
                srcEntry.EntryType == SqliteFileEntryType.Directory)
            {
                throw new FileNotFoundException($"源文件不存在或非文件类型: {srcPath}");
            }

            // 验证目标是否为目录
            if (DirectoryExists(destNormalized))
            {
                throw new IOException($"目标路径为目录: {destPath}");
            }
            // 处理目标文件已存在的情况
            bool targetFileExists = false;
            if (FileExists(destNormalized))
            {
                if (!overwrite)
                {
                    throw new IOException($"目标文件已存在且不允许覆盖: {destPath}");
                }
                targetFileExists = true;
            }
            //using var transaction = _connection.BeginTransaction();  // 开启事务
            try
            {
                // 允许覆盖时先删除目标文件
                if (targetFileExists)
                {
                    //DeleteFile(destNormalized);
                }
                // 获取源文件信息
                //var srcEntry = GetFileEntry(srcNormalized);

                // 创建目标文件条目（复制内容和元数据）
                var destEntry = new SqliteFileEntry
                {
                    Path = destNormalized,
                    Name = System.IO.Path.GetFileName(destNormalized),
                    EntryType = SqliteFileEntryType.File,
                    Content = srcEntry.Content,  // 复制文件内容
                    Size = srcEntry.Size,        // 复制文件大小
                    CreationTime = srcEntry.CreationTime,  // 保留源创建时间
                    LastAccessTime = DateTime.Now,         // 更新访问时间
                    LastWriteTime = DateTime.Now,          // 更新写入时间
                    Attributes = srcEntry.Attributes       // 复制文件属性
                };

                //    // 插入目标文件到数据库
                //    using var cmd = _connection.CreateCommand();
                //    cmd.CommandText = $@"
                //INSERT INTO {DatabaseTableName} (
                //    Path, Name, Type, Content, Size, 
                //    CreationTime, LastAccessTime, LastWriteTime, Attributes
                //) VALUES (
                //    @Path, @Name, @Type, @Content, @Size, 
                //    @CreationTime, @LastAccessTime, @LastWriteTime, @Attributes
                //)";

                //    cmd.Parameters.AddWithValue("@Path", destEntry.Path);
                //    cmd.Parameters.AddWithValue("@Name", destEntry.Name);
                //    cmd.Parameters.AddWithValue("@Type", (int)destEntry.Type);
                //    cmd.Parameters.AddWithValue("@Content", destEntry.Content == null ? DBNull.Value : destEntry.Content);
                //    cmd.Parameters.AddWithValue("@Size", destEntry.Size);
                //    cmd.Parameters.AddWithValue("@CreationTime", destEntry.CreationTime.ToUniversalTime());
                //    cmd.Parameters.AddWithValue("@LastAccessTime", destEntry.LastAccessTime.ToUniversalTime());
                //    cmd.Parameters.AddWithValue("@LastWriteTime", destEntry.LastWriteTime.ToUniversalTime());
                //    cmd.Parameters.AddWithValue("@Attributes", (int)destEntry.Attributes);

                //    cmd.ExecuteNonQuery();
                //    transaction.Commit();  // 提交事务
                if (targetFileExists)
                {
                    UpdateFileEntry(destEntry);
                }
                else
                {
                    CreateFileEntry(destEntry);
                }
            }
            catch
            {
                //transaction.Rollback();  // 失败回滚
                throw;
            }
        }



        public void ReplaceFile(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors)
        {
            // 1. Backup destination if needed
            if (!(destBackupPath.IsNull || destBackupPath.IsEmpty))
            {
                CopyFile(destPath, destBackupPath, true);
            }
            //using var transaction = _connection.BeginTransaction();
            try
            {
                // 2. Delete original destination
                DeleteFile(destPath);

                // 3. Move source to destination
                MoveFile(srcPath, destPath);

                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }

        public long GetFileLength(UPath path)
        {
            var normalizedPath = NormalizePath(path);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT Size FROM {DatabaseTableName} WHERE Path = @Path AND Type > @FileType";
            cmd.Parameters.AddWithValue("@Path", normalizedPath);
            cmd.Parameters.AddWithValue("@FileType", (int)SqliteFileEntryType.Directory);

            try
            {
                var result = cmd.ExecuteScalar();

                if (result == null || result is DBNull)
                    throw new FileNotFoundException($"文件不存在: {path}");

                return Convert.ToInt64(result);
            }
            catch (SqliteException ex)
            {
                throw new IOException($"获取文件长度失败: {path}", ex);
            }
        }

        public bool FileExists(UPath path)
        {
            return Exists(path, SqliteFileEntryType.File);
        }

        public void MoveFile(UPath srcPath, UPath destPath)
        {
            var srcNormalized = NormalizePath(srcPath);    // 标准化源路径
            var destNormalized = NormalizePath(destPath);  // 标准化目标路径
            var invalidPathChars = System.IO.Path.GetInvalidPathChars();
            if (destNormalized.IndexOfAny(invalidPathChars) != -1)
            {
                throw new Exception($"无效的文件目录：{destNormalized}");
            }
            var invalidFileChars = System.IO.Path.GetInvalidFileNameChars();
            if (Path.GetFileName(destNormalized).IndexOfAny(invalidFileChars) != -1)
            {
                throw new Exception($"无效的文件名：{destNormalized}");
            }
            //using var transaction = _connection.BeginTransaction();  // 开启事务
            try
            {
                // 1. 验证源文件存在且为文件类型
                if (!FileExists(srcNormalized))
                    throw new FileNotFoundException($"源文件不存在: {srcPath}");

                var srcEntry = GetFileEntry(srcNormalized);
                if (srcEntry.EntryType == SqliteFileEntryType.Directory)
                    throw new InvalidOperationException($"源路径不是文件: {srcPath}");

                // 2. 验证目标路径合法性
                //var destParent = (new UPath(destNormalized)).GetParent();
                //if (destParent != null && !DirectoryExists(destParent.ToString()))
                //    throw new DirectoryNotFoundException($"目标父目录不存在: {destParent}");

                if (Exists(destNormalized))
                    throw new IOException($"目标路径已存在: {destPath}");
                CheckDirectory(destPath.GetDirectory().FullName);
                // 3. 执行文件移动（更新路径和元数据）
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
            UPDATE {DatabaseTableName} SET 
                Path = @DestPath, 
                Name = @DestName, 
                LastAccessTime = @Now, 
                LastWriteTime = @Now 
            WHERE Path = @SrcPath";

                cmd.Parameters.AddWithValue("@DestPath", destNormalized);
                cmd.Parameters.AddWithValue("@DestName", destPath.GetName());
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@SrcPath", srcNormalized);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new FileNotFoundException($"源文件未找到: {srcPath}");

                //transaction.Commit();  // 提交事务
            }
            catch
            {
                //transaction.Rollback();  // 失败回滚
                throw;
            }
        }

        public void DeleteFile(UPath path)
        {
            var normalizedPath = NormalizePath(path); // 标准化路径
                                                      // 1. 验证文件存在且为文件类型
            if (!FileExists(normalizedPath))
                throw new FileNotFoundException($"文件不存在: {path}");

            var entry = GetFileEntry(normalizedPath);
            if (entry.EntryType == SqliteFileEntryType.Directory)
                throw new InvalidOperationException($"路径 {path} 不是文件");

            // 2. 权限检查（如只读属性）
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                throw new UnauthorizedAccessException($"文件为只读: {path}");
            }
            //using var transaction = _connection.BeginTransaction(); // 开启事务
            try
            {

                // 3. 执行删除操作
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM {DatabaseTableName} WHERE Path = @Path AND Type > @FileType";
                cmd.Parameters.AddWithValue("@Path", normalizedPath);
                cmd.Parameters.AddWithValue("@FileType", (int)SqliteFileEntryType.Directory);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new FileNotFoundException($"文件未找到或已被删除: {path}");

                //transaction.Commit(); // 提交事务
            }
            catch (SqliteException ex) //when (ex.SqliteErrorCode == (int)SqliteError.Constraint)
            {
                //transaction.Rollback();
                throw new IOException("删除文件时发生约束冲突（可能被其他对象引用）", ex);
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }

        public FileAttributes GetAttributes(UPath path)
        {
            var normalizedPath = NormalizePath(path);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        SELECT Attributes 
        FROM {DatabaseTableName} 
        WHERE Path = @Path";

            cmd.Parameters.AddWithValue("@Path", normalizedPath);

            try
            {
                var result = cmd.ExecuteScalar();

                if (result == null || result is DBNull)
                    throw new FileNotFoundException($"路径不存在: {path}");

                return (FileAttributes)Convert.ToInt32(result);
            }
            catch (SqliteException ex)
            {
                throw new IOException($"获取文件属性失败: {path}", ex);
            }
        }

        public void SetAttributes(UPath path, FileAttributes attributes)
        {
            var normalizedPath = NormalizePath(path);

            //using var transaction = _connection.BeginTransaction();
            try
            {
                // 验证路径是否存在
                if (!Exists(normalizedPath))
                    throw new FileNotFoundException($"路径不存在: {path}");

                // 执行属性更新
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
            UPDATE {DatabaseTableName} 
            SET Attributes = @Attributes, 
                LastWriteTime = @Now 
            WHERE Path = @Path";

                cmd.Parameters.AddWithValue("@Attributes", (int)attributes);
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@Path", normalizedPath);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new FileNotFoundException($"路径未找到: {path}");

                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }

        public DateTime GetCreationTime(UPath path)
        {
            var normalizedPath = NormalizePath(path);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        SELECT CreationTime 
        FROM {DatabaseTableName} 
        WHERE Path = @Path";

            cmd.Parameters.AddWithValue("@Path", normalizedPath);

            try
            {
                var result = cmd.ExecuteScalar();

                if (result == null || result is DBNull)
                    throw new FileNotFoundException($"路径不存在: {path}");

                // 从数据库读取的时间为 UTC，转换为本地时间返回
                return ((DateTime)result).ToLocalTime();
            }
            catch (SqliteException ex)
            {
                throw new IOException($"获取创建时间失败: {path}", ex);
            }
        }

        public void SetCreationTime(UPath path, DateTime time)
        {
            var normalizedPath = NormalizePath(path);

            //using var transaction = _connection.BeginTransaction();
            try
            {
                // 验证路径是否存在
                if (!Exists(normalizedPath))
                    throw new FileNotFoundException($"路径不存在: {path}");

                // 执行创建时间更新（存储为 UTC 时间）
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
            UPDATE {DatabaseTableName} 
            SET CreationTime = @CreationTime, 
                LastWriteTime = @Now 
            WHERE Path = @Path";

                cmd.Parameters.AddWithValue("@CreationTime", time.ToUniversalTime());
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@Path", normalizedPath);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new FileNotFoundException($"路径未找到: {path}");

                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }
        public DateTime GetLastAccessTime(UPath path)
        {
            var normalizedPath = NormalizePath(path);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        SELECT LastAccessTime 
        FROM {DatabaseTableName} 
        WHERE Path = @Path";

            cmd.Parameters.AddWithValue("@Path", normalizedPath);

            try
            {
                var result = cmd.ExecuteScalar();

                if (result == null || result is DBNull)
                    throw new FileNotFoundException($"路径不存在: {path}");

                // 从数据库读取的 UTC 时间转换为本地时间
                return ((DateTime)result).ToLocalTime();
            }
            catch (SqliteException ex)
            {
                throw new IOException($"获取访问时间失败: {path}", ex);
            }
        }
        public void SetLastAccessTime(UPath path, DateTime time)
        {
            var normalizedPath = NormalizePath(path);

            //using var transaction = _connection.BeginTransaction();
            try
            {
                if (!Exists(normalizedPath))
                    throw new FileNotFoundException($"路径不存在: {path}");

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
            UPDATE {DatabaseTableName} 
            SET LastAccessTime = @LastAccessTime 
            WHERE Path = @Path";

                cmd.Parameters.AddWithValue("@LastAccessTime", time.ToUniversalTime());
                cmd.Parameters.AddWithValue("@Path", normalizedPath);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new FileNotFoundException($"路径未找到: {path}");

                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }
        public DateTime GetLastWriteTime(UPath path)
        {
            var normalizedPath = NormalizePath(path);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        SELECT LastWriteTime 
        FROM {DatabaseTableName} 
        WHERE Path = @Path";

            cmd.Parameters.AddWithValue("@Path", normalizedPath);

            try
            {
                var result = cmd.ExecuteScalar();

                if (result == null || result is DBNull)
                    throw new FileNotFoundException($"路径不存在: {path}");

                // 从数据库读取的 UTC 时间转换为本地时间
                return ((DateTime)result).ToLocalTime();
            }
            catch (SqliteException ex)
            {
                throw new IOException($"获取写入时间失败: {path}", ex);
            }
        }
        public void SetLastWriteTime(UPath path, DateTime time)
        {
            var normalizedPath = NormalizePath(path);

            //using var transaction = _connection.BeginTransaction();
            try
            {
                if (!Exists(normalizedPath))
                    throw new FileNotFoundException($"路径不存在: {path}");

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
            UPDATE {DatabaseTableName} 
            SET LastWriteTime = @LastWriteTime 
            WHERE Path = @Path";

                cmd.Parameters.AddWithValue("@LastWriteTime", time.ToUniversalTime());
                cmd.Parameters.AddWithValue("@Path", normalizedPath);

                int rowsAffected = cmd.ExecuteNonQuery();
                if (rowsAffected == 0)
                    throw new FileNotFoundException($"路径未找到: {path}");

                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }

        public void CreateSymbolicLink(UPath path, UPath pathToTarget)
        {
            var normalizedPath = NormalizePath(path);
            var normalizedTarget = NormalizePath(pathToTarget);
            var invalidPathChars = System.IO.Path.GetInvalidPathChars();
            if (normalizedPath.IndexOfAny(invalidPathChars) != -1)
            {
                throw new Exception($"无效的文件目录：{normalizedPath}");
            }
            var invalidFileChars = System.IO.Path.GetInvalidFileNameChars();
            if (Path.GetFileName(normalizedPath).IndexOfAny(invalidFileChars) != -1)
            {
                throw new Exception($"无效的文件名：{normalizedPath}");
            }
            //using var transaction = _connection.BeginTransaction();
            try
            {
                // 验证目标路径是否存在
                if (!Exists(normalizedTarget))
                    throw new FileNotFoundException($"目标路径不存在: {pathToTarget}");

                // 验证符号链接路径是否已存在
                if (Exists(normalizedPath))
                    throw new IOException($"路径已存在: {path}");
                CheckDirectory(Path.GetDirectoryName(normalizedPath));
                // 验证符号链接路径的父目录是否存在
                //var parentPath = UPath.From(normalizedPath).GetParent();
                //if (parentPath != null && !DirectoryExists(parentPath.ToString()))
                //    throw new DirectoryNotFoundException($"父目录不存在: {parentPath}");

                // 创建符号链接条目
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $@"
            INSERT INTO {DatabaseTableName} (
                Path, Name, Type, Size, CreationTime, 
                LastAccessTime, LastWriteTime, Attributes, TargetPath
            ) VALUES (
                @Path, @Name, @Type, @Size, @CreationTime, 
                @LastAccessTime, @LastWriteTime, @Attributes, @TargetPath
            )";

                cmd.Parameters.AddWithValue("@Path", normalizedPath);
                cmd.Parameters.AddWithValue("@Name", path.GetName());
                cmd.Parameters.AddWithValue("@Type", (int)SqliteFileEntryType.SymbolicLink);
                cmd.Parameters.AddWithValue("@Size", 0); // 符号链接本身大小为0
                cmd.Parameters.AddWithValue("@CreationTime", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@LastAccessTime", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@LastWriteTime", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@Attributes", (int)(FileAttributes.ReparsePoint | FileAttributes.Normal));
                cmd.Parameters.AddWithValue("@TargetPath", normalizedTarget);

                cmd.ExecuteNonQuery();
                //transaction.Commit();
            }
            catch
            {
                //transaction.Rollback();
                throw;
            }
        }

        public bool TryResolveLinkTarget(UPath linkPath, out UPath resolvedPath)
        {
            //var entry = GetFileEntry(linkPath.FullName);
            //if (entry?.Type == FileSystemEntryType.SymbolicLink)
            //{
            //    resolvedPath = new UPath(entry.TargetPath);
            //    return true;
            //}
            //resolvedPath = UPath.Empty;
            //return false;
            resolvedPath = UPath.Empty;
            var normalizedPath = NormalizePath(linkPath);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
        SELECT TargetPath 
        FROM {DatabaseTableName} 
        WHERE Path = @Path AND Type = @LinkType";

            cmd.Parameters.AddWithValue("@Path", normalizedPath);
            cmd.Parameters.AddWithValue("@LinkType", (int)SqliteFileEntryType.SymbolicLink);

            try
            {
                var result = cmd.ExecuteScalar();

                if (result == null || result is DBNull)
                {
                    // 路径不存在或不是符号链接
                    return false;
                }

                resolvedPath = new UPath((string)result);
                return true;
            }
            catch (SqliteException)
            {
                // 数据库错误时返回失败
                return false;
            }
        }
        private string EscapeSqlWildcards(string input)
        {
            // 转义 SQL 特殊字符
            return input
                .Replace("[", "[[]")  // 转义 [
                .Replace("%", "[%]")  // 转义 %
                .Replace("_", "[_]"); // 转义 _
        }
        public IEnumerable<UPath> EnumeratePaths(UPath path, string searchPatternS, SearchOption searchOption, SearchTarget searchTarget)
        {
            var normalizedPath = NormalizePath(path);

            if (!normalizedPath.StartsWith('/'))
                throw new ArgumentException("无效的路径格式", nameof(path));
            if (string.IsNullOrEmpty(searchPatternS))
                throw new ArgumentException("搜索模式不能为空", nameof(searchPatternS));

            if (searchPatternS.Length > 256)
                throw new ArgumentException("搜索模式长度不能超过 256 个字符", nameof(searchPatternS));

            // 验证路径是否存在且为目录
            if (!DirectoryExists(normalizedPath))
                throw new DirectoryNotFoundException($"目录不存在: {path}");
            //
            var searchPattern = EscapeSqlWildcards(searchPatternS)
                       .Replace('*', '%')
                       .Replace('?', '_'); ;
            // 构建 SQL 查询条件
            var conditions = new List<string>();
            var parameters = new Dictionary<string, object>();

            // 基础路径条件
            if (normalizedPath == "/")
            {
                conditions.Add("Path LIKE '/' || @SearchPattern");
                parameters.Add("@SearchPattern", searchPattern);
            }
            else
            {
                conditions.Add("Path LIKE @BasePath || '/' || @SearchPattern");
                parameters.Add("@BasePath", normalizedPath);
                parameters.Add("@SearchPattern", searchPattern);
            }

            // 搜索深度条件
            if (searchOption == SearchOption.TopDirectoryOnly)
            {
                conditions.Add("(LENGTH(Path) - LENGTH(REPLACE(Path, '/', ''))) = @Depth");
                parameters.Add("@Depth", normalizedPath.Count(c => c == '/') + 1);
            }

            // 搜索目标条件
            switch (searchTarget)
            {
                case SearchTarget.Both:
                    conditions.Add("Type IN (0, 1, 2)"); // 目录和文件
                    break;
                case SearchTarget.Directory:
                    conditions.Add("Type = 0"); // 目录
                    break;
                case SearchTarget.File:
                    conditions.Add("Type > 0"); // 文件
                    break;
            }

            // 构建完整查询
            var query = $@"
        SELECT Path 
        FROM {DatabaseTableName} 
        WHERE {string.Join(" AND ", conditions)}
        ORDER BY Path";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = query;

            // 添加参数
            foreach (var param in parameters)
            {
                cmd.Parameters.AddWithValue(param.Key, param.Value);
            }

            // 执行查询并返回结果
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                yield return new UPath(reader.GetString(0));
            }
        }
        //public IEnumerable<UPath> EnumeratePaths2(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
        //{
        //    var pattern = ConvertSearchPatternToSql(searchPattern);
        //    var query = new StringBuilder($@"
        //WITH RECURSIVE FileTree(Path) AS (
        //    SELECT Path
        //    FROM {DatabaseTableName}
        //    WHERE Path LIKE @prefix || '%'
        //    UNION ALL
        //    SELECT fe.Path
        //    FROM {DatabaseTableName} fe
        //    JOIN FileTree ft ON fe.Path LIKE ft.Path || '/%'
        //)
        //SELECT Path FROM FileTree WHERE 1=1");

        //    if (searchTarget != SearchTarget.Both)
        //    {
        //        query.Append(" AND Type = @type");
        //    }

        //    using var command = _connection.CreateCommand();
        //    command.Parameters.AddWithValue("@prefix", path.FullName);
        //    command.Parameters.AddWithValue("@type", (int)searchTarget);
        //    command.CommandText = query.ToString();

        //    using var reader = command.ExecuteReader();
        //    while (reader.Read())
        //    {
        //        var currentPath = new UPath(reader.GetString(0));
        //        if (MatchesPattern(currentPath.GetName(), pattern))
        //        {
        //            yield return currentPath;
        //        }
        //    }
        //}
        //private bool MatchesPattern(string fileName, string pattern)
        //{
        //    return fileName.Contains(pattern);
        //}
        //private string ConvertSearchPatternToSql(string pattern)
        //{
        //    return pattern
        //        .Replace(".", "\\.")
        //        .Replace("*", ".*")
        //        .Replace("?", ".")
        //        .TrimStart('^')
        //        .TrimEnd('$');
        //}

        public IEnumerable<FileSystemItem> EnumerateItems(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate = null)
        {
            var normalizedPath = NormalizePath(path);

            // 验证路径是否存在且为目录
            if (!DirectoryExists(normalizedPath))
                throw new DirectoryNotFoundException($"目录不存在: {path}");

            // 构建基础查询
            var query = new StringBuilder($@"
        SELECT Path, Name, Type, Size, CreationTime, LastAccessTime, LastWriteTime, Attributes 
        FROM {DatabaseTableName} 
        WHERE 1=1");

            // 添加路径条件
            if (normalizedPath == "/")
            {
                query.Append(" AND Path LIKE '/%'");
            }
            else
            {
                query.Append(" AND Path LIKE @BasePath || '/%'");
            }

            // 添加搜索深度条件
            if (searchOption == SearchOption.TopDirectoryOnly)
            {
                query.Append(" AND (LENGTH(Path) - LENGTH(REPLACE(Path, '/', ''))) = @Depth");
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = query.ToString();

            // 添加参数
            if (normalizedPath != "/")
            {
                cmd.Parameters.AddWithValue("@BasePath", normalizedPath);
            }

            if (searchOption == SearchOption.TopDirectoryOnly)
            {
                cmd.Parameters.AddWithValue("@Depth", normalizedPath.Count(c => c == '/') + 1);
            }

            // 执行查询并应用谓词过滤
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {

                var fitem = new SqliteFileEntry
                {
                    Path = reader.GetString(0),
                    Name = reader.GetString(1),
                    EntryType = (SqliteFileEntryType)reader.GetInt32(2),
                    //Content = reader.GetValue(3) as byte[] ?? Array.Empty<byte>(),
                    Size = reader.GetInt64(3),
                    CreationTime = reader.GetDateTime(4),
                    LastAccessTime = reader.GetDateTime(5),
                    LastWriteTime = reader.GetDateTime(6),
                    Attributes = (FileAttributes)reader.GetInt32(7),
                    //TargetPath = reader.IsDBNull(9) ? null : reader.GetString(9)
                };
                FileSystemItem item = new FileSystemItem(this, fitem.Path, fitem.EntryType == SqliteFileEntryType.Directory);

                // 应用搜索谓词（如果有）
                if (searchPredicate == null || searchPredicate(ref item))
                {
                    yield return item;
                }
            }
        }

        public bool CanWatch(UPath path)
        {
            return false;
        }

        public IFileSystemWatcher Watch(UPath path)
        {
            throw new NotImplementedException();
        }


        public UPath ConvertPathFromInternal(string systemPath)
        {
            if (systemPath == null)
            {
                throw new ArgumentNullException("systemPath");
            }
            return ValidatePath(systemPath);
        }
        public string ConvertPathToInternal(UPath path)
        {
            return ValidatePath(path).FullName;
        }

        public (IFileSystem FileSystem, UPath Path) ResolvePath(UPath path)
        {
            return (this, ValidatePath(path));
        }
        protected UPath ValidatePath(UPath path, string name = "path", bool allowNull = false)
        {
            if (allowNull && path.IsNull)
            {
                return path;
            }
            string fullName = path.FullName;
            for (int i = 0; i < fullName.Length; i++)
            {
                char c = fullName[i];
                if (char.IsControl(c))
                {
                    throw new ArgumentException($"Invalid character found \\u{(int)c:X4} at index {i}", "path");
                }
            }
            path.AssertAbsolute(name);
            return ValidatePathImpl(path, name);
        }
        protected virtual UPath ValidatePathImpl(UPath path, string name = "path")
        {
            if (path.FullName.IndexOf(':') >= 0)
            {
                throw new NotSupportedException($"The path `{path}` cannot contain the `:` character");
            }
            return path;
        }
        // 其他IFileSystem接口方法的实现需按照类似逻辑补充
        private (UPath Directory, string Name) SplitPath(UPath path)
        {
            var directory = path.GetDirectory();
            var name = path.GetName();
            return (directory, name);
        }



    }

    public class SqliteFileEntry
    {
        public string Path { get; set; }
        //public string ParentPath { get; set; }
        public string Name { get; set; }
        public SqliteFileEntryType EntryType { get; set; }
        public byte[] Content { get; set; }
        public long Size { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastAccessTime { get; set; }
        public DateTime LastWriteTime { get; set; }
        public FileAttributes Attributes { get; set; }
        public string TargetPath { get; set; }
    }

    public enum SqliteFileEntryType
    {
        Directory = 0,
        File,
        SymbolicLink
    }
    public class SqliteFileMemoryStream : MemoryStream
    {
        public Action<SqliteFileMemoryStream> OnWriteFlush;
        public SqliteFileMemoryStream() : base()
        {
        }
        public SqliteFileMemoryStream(int capacity) : base(capacity)
        {
        }
        public SqliteFileMemoryStream(byte[] buffer)
        : base(buffer)
        {

        }
        public SqliteFileMemoryStream(byte[] buffer, bool writable)
        : base(buffer, writable)
        {

        }
        public SqliteFileMemoryStream(byte[] buffer, int index, int count)
          : base(buffer, index, count)
        {

        }
        public SqliteFileMemoryStream(byte[] buffer, int index, int count, bool writable)
            : base(buffer, index, count, writable)
        {

        }
        public SqliteFileMemoryStream(byte[] buffer, int index, int count, bool writable, bool publiclyVisible)
            : base(buffer, index, count, writable, publiclyVisible)
        {

        }
        public override void Flush()
        {
            base.Flush();
            OnWriteFlush?.Invoke(this);
        }


    }

}
