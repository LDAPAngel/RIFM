﻿
namespace RIFMConsole
{
    partial class frmSettings
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(frmSettings));
            this.label1 = new System.Windows.Forms.Label();
            this.cmdOK = new System.Windows.Forms.Button();
            this.txtUDPPort = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.txtCommonEpoch = new System.Windows.Forms.TextBox();
            this.label3 = new System.Windows.Forms.Label();
            this.txtDSRMPassword = new System.Windows.Forms.TextBox();
            this.cmdClose = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(11, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(72, 17);
            this.label1.TabIndex = 0;
            this.label1.Text = "UDP Port";
            // 
            // cmdOK
            // 
            this.cmdOK.Location = new System.Drawing.Point(14, 140);
            this.cmdOK.Name = "cmdOK";
            this.cmdOK.Size = new System.Drawing.Size(69, 32);
            this.cmdOK.TabIndex = 1;
            this.cmdOK.Text = "OK";
            this.cmdOK.UseVisualStyleBackColor = true;
            this.cmdOK.Click += new System.EventHandler(this.cmdOK_Click);
            // 
            // txtUDPPort
            // 
            this.txtUDPPort.Location = new System.Drawing.Point(152, 6);
            this.txtUDPPort.Name = "txtUDPPort";
            this.txtUDPPort.Size = new System.Drawing.Size(166, 23);
            this.txtUDPPort.TabIndex = 2;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(11, 48);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(104, 17);
            this.label2.TabIndex = 3;
            this.label2.Text = "Common ePoch";
            // 
            // txtCommonEpoch
            // 
            this.txtCommonEpoch.Location = new System.Drawing.Point(152, 45);
            this.txtCommonEpoch.Name = "txtCommonEpoch";
            this.txtCommonEpoch.Size = new System.Drawing.Size(166, 23);
            this.txtCommonEpoch.TabIndex = 4;
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(11, 93);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(112, 17);
            this.label3.TabIndex = 5;
            this.label3.Text = "DSRM Password";
            // 
            // txtDSRMPassword
            // 
            this.txtDSRMPassword.Location = new System.Drawing.Point(152, 90);
            this.txtDSRMPassword.Name = "txtDSRMPassword";
            this.txtDSRMPassword.Size = new System.Drawing.Size(166, 23);
            this.txtDSRMPassword.TabIndex = 6;
            // 
            // cmdClose
            // 
            this.cmdClose.Location = new System.Drawing.Point(249, 141);
            this.cmdClose.Name = "cmdClose";
            this.cmdClose.Size = new System.Drawing.Size(69, 32);
            this.cmdClose.TabIndex = 7;
            this.cmdClose.Text = "Close";
            this.cmdClose.UseVisualStyleBackColor = true;
            this.cmdClose.Click += new System.EventHandler(this.cmdClose_Click);
            // 
            // frmSettings
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(330, 185);
            this.Controls.Add(this.cmdClose);
            this.Controls.Add(this.txtDSRMPassword);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.txtCommonEpoch);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.txtUDPPort);
            this.Controls.Add(this.cmdOK);
            this.Controls.Add(this.label1);
            this.Font = new System.Drawing.Font("Courier New", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "frmSettings";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Settings";
            this.Load += new System.EventHandler(this.frmSettings_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Button cmdOK;
        private System.Windows.Forms.TextBox txtUDPPort;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox txtCommonEpoch;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TextBox txtDSRMPassword;
        private System.Windows.Forms.Button cmdClose;
    }
}