namespace DesktopPet
{
    partial class FormHelp
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FormHelp));
            this.onlineDocumentationLink = new System.Windows.Forms.LinkLabel();
            this.helpText = new System.Windows.Forms.RichTextBox();
            this.SuspendLayout();
            // 
            // onlineDocumentationLink
            // 
            this.onlineDocumentationLink.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.onlineDocumentationLink.LinkBehavior = System.Windows.Forms.LinkBehavior.HoverUnderline;
            this.onlineDocumentationLink.Location = new System.Drawing.Point(0, 394);
            this.onlineDocumentationLink.Name = "onlineDocumentationLink";
            this.onlineDocumentationLink.Padding = new System.Windows.Forms.Padding(12, 8, 12, 8);
            this.onlineDocumentationLink.Size = new System.Drawing.Size(721, 36);
            this.onlineDocumentationLink.TabIndex = 1;
            this.onlineDocumentationLink.TabStop = true;
            this.onlineDocumentationLink.Text = "Open current online documentation (HTTPS)";
            this.onlineDocumentationLink.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.onlineDocumentationLink.LinkClicked += new System.Windows.Forms.LinkLabelLinkClickedEventHandler(this.OnlineDocumentationLink_LinkClicked);
            //
            // helpText
            //
            this.helpText.BackColor = System.Drawing.SystemColors.Window;
            this.helpText.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.helpText.DetectUrls = true;
            this.helpText.Dock = System.Windows.Forms.DockStyle.Fill;
            this.helpText.Location = new System.Drawing.Point(0, 0);
            this.helpText.Name = "helpText";
            this.helpText.ReadOnly = true;
            this.helpText.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;
            this.helpText.Size = new System.Drawing.Size(721, 394);
            this.helpText.TabIndex = 0;
            this.helpText.Text = "";
            this.helpText.LinkClicked += new System.Windows.Forms.LinkClickedEventHandler(this.HelpText_LinkClicked);
            // 
            // FormHelp
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(721, 430);
            this.Controls.Add(this.helpText);
            this.Controls.Add(this.onlineDocumentationLink);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MinimumSize = new System.Drawing.Size(560, 360);
            this.Name = "FormHelp";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "DesktopPet Help";
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.LinkLabel onlineDocumentationLink;
        private System.Windows.Forms.RichTextBox helpText;
    }
}
