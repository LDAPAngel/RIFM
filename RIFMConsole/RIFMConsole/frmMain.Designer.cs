
namespace RIFMConsole
{
    partial class frmMain
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(frmMain));
            this.imageList1 = new System.Windows.Forms.ImageList(this.components);
            this.cmdAbort = new System.Windows.Forms.Button();
            this.cmdSettings = new System.Windows.Forms.Button();
            this.chkAutoScroll = new System.Windows.Forms.CheckBox();
            this.cmdResume = new System.Windows.Forms.Button();
            this.lblCompleted = new System.Windows.Forms.Label();
            this.cmdStart = new System.Windows.Forms.Button();
            this.cmdDeploy = new System.Windows.Forms.Button();
            this.cmdDefine = new System.Windows.Forms.Button();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.tabPage2 = new System.Windows.Forms.TabPage();
            this.tabControl1.SuspendLayout();
            this.SuspendLayout();
            // 
            // imageList1
            // 
            this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageList1.ImageStream")));
            this.imageList1.TransparentColor = System.Drawing.Color.Transparent;
            this.imageList1.Images.SetKeyName(0, "status");
            this.imageList1.Images.SetKeyName(1, "reboot");
            this.imageList1.Images.SetKeyName(2, "completed");
            this.imageList1.Images.SetKeyName(3, "deploy");
            // 
            // cmdAbort
            // 
            this.cmdAbort.Enabled = false;
            this.cmdAbort.Location = new System.Drawing.Point(257, 12);
            this.cmdAbort.Margin = new System.Windows.Forms.Padding(2, 3, 2, 3);
            this.cmdAbort.Name = "cmdAbort";
            this.cmdAbort.Size = new System.Drawing.Size(67, 25);
            this.cmdAbort.TabIndex = 12;
            this.cmdAbort.Text = "Abort";
            this.cmdAbort.UseVisualStyleBackColor = true;
            this.cmdAbort.Click += new System.EventHandler(this.cmdAbort_Click);
            // 
            // cmdSettings
            // 
            this.cmdSettings.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.cmdSettings.Location = new System.Drawing.Point(625, 15);
            this.cmdSettings.Margin = new System.Windows.Forms.Padding(2, 3, 2, 3);
            this.cmdSettings.Name = "cmdSettings";
            this.cmdSettings.Size = new System.Drawing.Size(85, 25);
            this.cmdSettings.TabIndex = 11;
            this.cmdSettings.Text = "Settings";
            this.cmdSettings.UseVisualStyleBackColor = true;
            this.cmdSettings.Click += new System.EventHandler(this.cmdSettings_Click);
            // 
            // chkAutoScroll
            // 
            this.chkAutoScroll.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.chkAutoScroll.AutoSize = true;
            this.chkAutoScroll.Checked = true;
            this.chkAutoScroll.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chkAutoScroll.Location = new System.Drawing.Point(720, 19);
            this.chkAutoScroll.Name = "chkAutoScroll";
            this.chkAutoScroll.Size = new System.Drawing.Size(107, 21);
            this.chkAutoScroll.TabIndex = 10;
            this.chkAutoScroll.Text = "AutoScroll";
            this.chkAutoScroll.UseVisualStyleBackColor = true;
            // 
            // cmdResume
            // 
            this.cmdResume.Location = new System.Drawing.Point(339, 12);
            this.cmdResume.Margin = new System.Windows.Forms.Padding(2, 3, 2, 3);
            this.cmdResume.Name = "cmdResume";
            this.cmdResume.Size = new System.Drawing.Size(67, 25);
            this.cmdResume.TabIndex = 9;
            this.cmdResume.Text = "Resume";
            this.cmdResume.UseVisualStyleBackColor = true;
            this.cmdResume.Visible = false;
            this.cmdResume.Click += new System.EventHandler(this.cmdResume_Click);
            // 
            // lblCompleted
            // 
            this.lblCompleted.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblCompleted.AutoSize = true;
            this.lblCompleted.Font = new System.Drawing.Font("Courier New", 15F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblCompleted.ForeColor = System.Drawing.Color.Green;
            this.lblCompleted.Location = new System.Drawing.Point(493, 15);
            this.lblCompleted.Name = "lblCompleted";
            this.lblCompleted.Size = new System.Drawing.Size(118, 22);
            this.lblCompleted.TabIndex = 7;
            this.lblCompleted.Text = "Completed";
            this.lblCompleted.Visible = false;
            // 
            // cmdStart
            // 
            this.cmdStart.Location = new System.Drawing.Point(175, 12);
            this.cmdStart.Margin = new System.Windows.Forms.Padding(2, 3, 2, 3);
            this.cmdStart.Name = "cmdStart";
            this.cmdStart.Size = new System.Drawing.Size(67, 25);
            this.cmdStart.TabIndex = 6;
            this.cmdStart.Text = "Start";
            this.cmdStart.UseVisualStyleBackColor = true;
            this.cmdStart.Click += new System.EventHandler(this.cmdStart_Click);
            // 
            // cmdDeploy
            // 
            this.cmdDeploy.Location = new System.Drawing.Point(93, 12);
            this.cmdDeploy.Margin = new System.Windows.Forms.Padding(2, 3, 2, 3);
            this.cmdDeploy.Name = "cmdDeploy";
            this.cmdDeploy.Size = new System.Drawing.Size(67, 25);
            this.cmdDeploy.TabIndex = 5;
            this.cmdDeploy.Text = "Deploy";
            this.cmdDeploy.UseVisualStyleBackColor = true;
            this.cmdDeploy.Click += new System.EventHandler(this.cmdDeploy_Click);
            // 
            // cmdDefine
            // 
            this.cmdDefine.Location = new System.Drawing.Point(11, 12);
            this.cmdDefine.Margin = new System.Windows.Forms.Padding(2, 3, 2, 3);
            this.cmdDefine.Name = "cmdDefine";
            this.cmdDefine.Size = new System.Drawing.Size(67, 25);
            this.cmdDefine.TabIndex = 4;
            this.cmdDefine.Text = "Define";
            this.cmdDefine.UseVisualStyleBackColor = true;
            this.cmdDefine.Click += new System.EventHandler(this.cmdDefine_Click);
            // 
            // tabControl1
            // 
            this.tabControl1.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Controls.Add(this.tabPage2);
            this.tabControl1.Location = new System.Drawing.Point(6, 55);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(825, 476);
            this.tabControl1.TabIndex = 5;
            this.tabControl1.DrawItem += new System.Windows.Forms.DrawItemEventHandler(this.tabControl1_DrawItem);
            this.tabControl1.Selected += new System.Windows.Forms.TabControlEventHandler(this.tabControl1_Selected);
            // 
            // tabPage1
            // 
            this.tabPage1.Location = new System.Drawing.Point(4, 25);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(817, 447);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "tabPage1";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // tabPage2
            // 
            this.tabPage2.Location = new System.Drawing.Point(4, 22);
            this.tabPage2.Name = "tabPage2";
            this.tabPage2.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage2.Size = new System.Drawing.Size(748, 338);
            this.tabPage2.TabIndex = 1;
            this.tabPage2.Text = "tabPage2";
            this.tabPage2.UseVisualStyleBackColor = true;
            // 
            // frmMain
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(838, 543);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.cmdAbort);
            this.Controls.Add(this.cmdSettings);
            this.Controls.Add(this.cmdDefine);
            this.Controls.Add(this.chkAutoScroll);
            this.Controls.Add(this.cmdDeploy);
            this.Controls.Add(this.cmdResume);
            this.Controls.Add(this.cmdStart);
            this.Controls.Add(this.lblCompleted);
            this.Font = new System.Drawing.Font("Courier New", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.Name = "frmMain";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "RIFM Console";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.frmMain_FormClosing);
            this.Load += new System.EventHandler(this.frmMain_Load);
            this.tabControl1.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.ImageList imageList1;
        private System.Windows.Forms.Button cmdDefine;
        private System.Windows.Forms.Button cmdStart;
        private System.Windows.Forms.Button cmdDeploy;
        private System.Windows.Forms.Label lblCompleted;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.TabPage tabPage2;
        private System.Windows.Forms.Button cmdResume;
        private System.Windows.Forms.CheckBox chkAutoScroll;
        private System.Windows.Forms.Button cmdSettings;
        private System.Windows.Forms.Button cmdAbort;
    }
}

