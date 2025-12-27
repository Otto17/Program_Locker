namespace Program_Locker
{
    partial class ProgramForm
    {
        /// <summary>
        /// Обязательная переменная конструктора.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Освободить все используемые ресурсы.
        /// </summary>
        /// <param name="disposing">истинно, если управляемый ресурс должен быть удален; иначе ложно.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Код, автоматически созданный конструктором форм Windows

        /// <summary>
        /// Требуемый метод для поддержки конструктора — не изменяйте 
        /// содержимое этого метода с помощью редактора кода.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ProgramForm));
            this.lblPass = new System.Windows.Forms.Label();
            this.tbPass = new System.Windows.Forms.TextBox();
            this.btnSetPass = new System.Windows.Forms.Button();
            this.lblList = new System.Windows.Forms.Label();
            this.btnLock = new System.Windows.Forms.Button();
            this.btnAddFile = new System.Windows.Forms.Button();
            this.btnUnlock = new System.Windows.Forms.Button();
            this.btnRemoveEntry = new System.Windows.Forms.Button();
            this.btnRefresh = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            this.lblNotes = new System.Windows.Forms.Label();
            this.lvFiles = new System.Windows.Forms.ListView();
            this.colProgram = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.colStatus = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.colHidden = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.Version = new System.Windows.Forms.Label();
            this.Author = new System.Windows.Forms.LinkLabel();
            this.SuspendLayout();
            // 
            // lblPass
            // 
            this.lblPass.AutoSize = true;
            this.lblPass.Location = new System.Drawing.Point(12, 12);
            this.lblPass.Name = "lblPass";
            this.lblPass.Size = new System.Drawing.Size(87, 13);
            this.lblPass.TabIndex = 0;
            this.lblPass.Text = "Мастер-пароль:";
            // 
            // tbPass
            // 
            this.tbPass.Location = new System.Drawing.Point(12, 32);
            this.tbPass.Name = "tbPass";
            this.tbPass.Size = new System.Drawing.Size(300, 20);
            this.tbPass.TabIndex = 1;
            this.tbPass.UseSystemPasswordChar = true;
            // 
            // btnSetPass
            // 
            this.btnSetPass.Location = new System.Drawing.Point(320, 30);
            this.btnSetPass.Name = "btnSetPass";
            this.btnSetPass.Size = new System.Drawing.Size(180, 23);
            this.btnSetPass.TabIndex = 2;
            this.btnSetPass.Text = "Установить / Изменить";
            this.btnSetPass.UseVisualStyleBackColor = true;
            this.btnSetPass.Click += new System.EventHandler(this.BtnSetPass_Click);
            // 
            // lblList
            // 
            this.lblList.AutoSize = true;
            this.lblList.Location = new System.Drawing.Point(12, 70);
            this.lblList.Name = "lblList";
            this.lblList.Size = new System.Drawing.Size(141, 13);
            this.lblList.TabIndex = 0;
            this.lblList.Text = "Защищённые программы:";
            // 
            // btnLock
            // 
            this.btnLock.Location = new System.Drawing.Point(170, 300);
            this.btnLock.Name = "btnLock";
            this.btnLock.Size = new System.Drawing.Size(140, 23);
            this.btnLock.TabIndex = 5;
            this.btnLock.Text = "Заблокировать";
            this.btnLock.UseVisualStyleBackColor = true;
            this.btnLock.Click += new System.EventHandler(this.BtnLock_Click);
            // 
            // btnAddFile
            // 
            this.btnAddFile.Location = new System.Drawing.Point(12, 300);
            this.btnAddFile.Name = "btnAddFile";
            this.btnAddFile.Size = new System.Drawing.Size(150, 23);
            this.btnAddFile.TabIndex = 4;
            this.btnAddFile.Text = "Добавить файлы...";
            this.btnAddFile.UseVisualStyleBackColor = true;
            this.btnAddFile.Click += new System.EventHandler(this.BtnAddFile_Click);
            // 
            // btnUnlock
            // 
            this.btnUnlock.Location = new System.Drawing.Point(318, 300);
            this.btnUnlock.Name = "btnUnlock";
            this.btnUnlock.Size = new System.Drawing.Size(140, 23);
            this.btnUnlock.TabIndex = 6;
            this.btnUnlock.Text = "Разблокировать";
            this.btnUnlock.UseVisualStyleBackColor = true;
            this.btnUnlock.Click += new System.EventHandler(this.BtnUnlock_Click);
            // 
            // btnRemoveEntry
            // 
            this.btnRemoveEntry.Location = new System.Drawing.Point(466, 300);
            this.btnRemoveEntry.Name = "btnRemoveEntry";
            this.btnRemoveEntry.Size = new System.Drawing.Size(130, 23);
            this.btnRemoveEntry.TabIndex = 7;
            this.btnRemoveEntry.Text = "Удалить запись";
            this.btnRemoveEntry.UseVisualStyleBackColor = true;
            this.btnRemoveEntry.Click += new System.EventHandler(this.BtnRemoveEntry_Click);
            // 
            // btnRefresh
            // 
            this.btnRefresh.Location = new System.Drawing.Point(604, 300);
            this.btnRefresh.Name = "btnRefresh";
            this.btnRefresh.Size = new System.Drawing.Size(128, 23);
            this.btnRefresh.TabIndex = 8;
            this.btnRefresh.Text = "Обновить список";
            this.btnRefresh.UseVisualStyleBackColor = true;
            this.btnRefresh.Click += new System.EventHandler(this.BtnRefresh_Click);
            // 
            // lblStatus
            // 
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(12, 340);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(53, 13);
            this.lblStatus.TabIndex = 0;
            this.lblStatus.Text = "Статус: —";
            // 
            // lblNotes
            // 
            this.lblNotes.AutoSize = true;
            this.lblNotes.ForeColor = System.Drawing.Color.DarkGreen;
            this.lblNotes.Location = new System.Drawing.Point(12, 370);
            this.lblNotes.Name = "lblNotes";
            this.lblNotes.Size = new System.Drawing.Size(0, 13);
            this.lblNotes.TabIndex = 10;
            this.lblNotes.Visible = false;
            // 
            // lvFiles
            // 
            this.lvFiles.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.colProgram,
            this.colStatus,
            this.colHidden});
            this.lvFiles.FullRowSelect = true;
            this.lvFiles.GridLines = true;
            this.lvFiles.HideSelection = false;
            this.lvFiles.Location = new System.Drawing.Point(12, 90);
            this.lvFiles.Name = "lvFiles";
            this.lvFiles.Size = new System.Drawing.Size(720, 200);
            this.lvFiles.TabIndex = 3;
            this.lvFiles.UseCompatibleStateImageBehavior = false;
            this.lvFiles.View = System.Windows.Forms.View.Details;
            this.lvFiles.SelectedIndexChanged += new System.EventHandler(this.LvFiles_SelectedIndexChanged);
            // 
            // colProgram
            // 
            this.colProgram.Text = "Программа";
            this.colProgram.Width = 300;
            // 
            // colStatus
            // 
            this.colStatus.Text = "Статус";
            this.colStatus.Width = 115;
            // 
            // colHidden
            // 
            this.colHidden.Text = "Скрытый файл";
            this.colHidden.Width = 300;
            // 
            // Version
            // 
            this.Version.AutoSize = true;
            this.Version.Font = new System.Drawing.Font("Microsoft Sans Serif", 6.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(204)));
            this.Version.Location = new System.Drawing.Point(625, 442);
            this.Version.Name = "Version";
            this.Version.Size = new System.Drawing.Size(57, 12);
            this.Version.TabIndex = 0;
            this.Version.Text = "ver 27.12.25";
            // 
            // Author
            // 
            this.Author.AutoSize = true;
            this.Author.Font = new System.Drawing.Font("Microsoft Sans Serif", 6.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(204)));
            this.Author.Location = new System.Drawing.Point(688, 442);
            this.Author.Name = "Author";
            this.Author.Size = new System.Drawing.Size(51, 12);
            this.Author.TabIndex = 9;
            this.Author.TabStop = true;
            this.Author.Text = "Автор Otto";
            this.Author.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.Author_LinkClicked);
            // 
            // ProgramForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(743, 460);
            this.Controls.Add(this.Author);
            this.Controls.Add(this.Version);
            this.Controls.Add(this.lvFiles);
            this.Controls.Add(this.lblNotes);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnRefresh);
            this.Controls.Add(this.btnRemoveEntry);
            this.Controls.Add(this.btnUnlock);
            this.Controls.Add(this.btnAddFile);
            this.Controls.Add(this.btnLock);
            this.Controls.Add(this.lblList);
            this.Controls.Add(this.btnSetPass);
            this.Controls.Add(this.tbPass);
            this.Controls.Add(this.lblPass);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.Name = "ProgramForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Program Locker - Менеджер защиты исполняемых файлов";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblPass;
        private System.Windows.Forms.TextBox tbPass;
        private System.Windows.Forms.Button btnSetPass;
        private System.Windows.Forms.Label lblList;
        private System.Windows.Forms.Button btnLock;
        private System.Windows.Forms.Button btnAddFile;
        private System.Windows.Forms.Button btnUnlock;
        private System.Windows.Forms.Button btnRemoveEntry;
        private System.Windows.Forms.Button btnRefresh;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Label lblNotes;
        private System.Windows.Forms.ListView lvFiles;
        private System.Windows.Forms.ColumnHeader colProgram;
        private System.Windows.Forms.ColumnHeader colStatus;
        private System.Windows.Forms.ColumnHeader colHidden;
        private System.Windows.Forms.Label Version;
        private System.Windows.Forms.LinkLabel Author;
    }
}

