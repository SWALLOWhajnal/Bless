// created on 12/4/2006 at 1:25 AM
/*
 *   Copyright (c) 2006, Alexandros Frantzis (alf82 [at] freemail [dot] gr)
 *
 *   This file is part of Bless.
 *
 *   Bless is free software; you can redistribute it and/or modify
 *   it under the terms of the GNU General Public License as published by
 *   the Free Software Foundation; either version 2 of the License, or
 *   (at your option) any later version.
 *
 *   Bless is distributed in the hope that it will be useful,
 *   but WITHOUT ANY WARRANTY; without even the implied warranty of
 *   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *   GNU General Public License for more details.
 *
 *   You should have received a copy of the GNU General Public License
 *   along with Bless; if not, write to the Free Software
 *   Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */
using System;
using System.Threading;
using System.IO;
using Gtk;
using Bless.Plugins;
using Bless.Tools.Export;
using Bless.Gui;
using Bless.Util;
using Bless.Buffers;

namespace Bless.Gui.Dialogs
{

public class ExportDialog : Dialog
{
	PluginManager pluginManager;
	DataBook dataBook;
	Gtk.Window mainWindow;
	
	[Glade.Widget] Gtk.VBox ExportDialogVBox;
	[Glade.Widget] Gtk.ComboBox ExportAsCombo;
	[Glade.Widget] Gtk.Entry ExportPatternEntry;
	[Glade.Widget] Gtk.ProgressBar ExportProgressBar;
	[Glade.Widget] Gtk.Entry ExportFileEntry;
	[Glade.Widget] Gtk.RadioButton WholeFileRadio;
	[Glade.Widget] Gtk.RadioButton CurrentSelectionRadio;
	[Glade.Widget] Gtk.RadioButton RangeRadio;
	[Glade.Widget] Gtk.Entry RangeFromEntry;
	[Glade.Widget] Gtk.Entry RangeToEntry;
	[Glade.Widget] Gtk.HBox ProgressHBox;
	Gtk.Button CloseButton;
	Gtk.Button ExportButton;
	
	AutoResetEvent exportFinishedEvent;
	readonly public object LockObj=new object();
	
	bool cancelClicked;
	
	public ExportDialog(DataBook db, Gtk.Window mw)
	: base("Export Bytes", null, 0) 
	{
		Glade.XML gxml = new Glade.XML (FileResourcePath.GetSystemPath("..","data","bless.glade"), "ExportDialogVBox", null);
		gxml.Autoconnect (this);
		
		dataBook = db;
		mainWindow = mw;
		pluginManager = new PluginManager(typeof(ExportPlugin), new object[0]);
		exportFinishedEvent = new AutoResetEvent(false);
		
		SetupExportPlugins();
		
		ProgressHBox.Visible = false;
		cancelClicked = false;
		
		this.Modal = false;
		this.BorderWidth = 6;
		this.HasSeparator = false;
		CloseButton = (Gtk.Button)this.AddButton(Gtk.Stock.Close, ResponseType.Close);
		ExportButton = (Gtk.Button)this.AddButton("Export", ResponseType.Ok);
		this.Response += new ResponseHandler(OnDialogResponse);
		this.VBox.Add(ExportDialogVBox);
	}
	
	private void SetupExportPlugins()
	{
		System.Console.WriteLine("Setup export dialog");
		ListStore model = new ListStore(typeof(string), typeof(ExportPlugin));
		 
		foreach(ExportPlugin plugin in pluginManager.Plugins) {
			System.Console.WriteLine("Found plugin {0}", plugin.Description);
			model.AppendValues(plugin.Description, plugin);
		}
		
		Gtk.CellRenderer renderer = new Gtk.CellRendererText();
		
		ExportAsCombo.PackStart(renderer, false);
		ExportAsCombo.AddAttribute(renderer, "text", 0);
		ExportAsCombo.Model = model;
		ExportAsCombo.Active = 0;
	}
	
	private Util.Range GetCurrentRange(DataView dv)
	{
		if (WholeFileRadio.Active == true)
			return new Util.Range(0, dv.Buffer.Size - 1);
		else if (CurrentSelectionRadio.Active == true)
			return dv.Selection;
		else if (RangeRadio.Active == true)
			return new Util.Range(BaseConverter.Parse(RangeFromEntry.Text), BaseConverter.Parse(RangeToEntry.Text));
	
		return new Util.Range();
	}
	
	private void OnSelectFileButtonClicked(object o, EventArgs args)
	{
		FileChooserDialog fcd = new FileChooserDialog("Select file", mainWindow, FileChooserAction.Save,  "Cancel", ResponseType.Cancel,
                                      "Select", ResponseType.Accept);
		if ((ResponseType)fcd.Run() == ResponseType.Accept)
			ExportFileEntry.Text = fcd.Filename;
		fcd.Destroy();
	}
	
	private void OnRangeRadioToggled(object o, EventArgs args)
	{
		RangeFromEntry.Sensitive = RangeRadio.Active;
		RangeToEntry.Sensitive = RangeRadio.Active;
	}
	
	private void OnExportCancelClicked(object o, EventArgs args)
	{
		cancelClicked = true;
	}
	
	private IAsyncResult BeginExport(IExporter exporter, IBuffer buf, long start, long end)
	{
		exportFinishedEvent.Reset();
		
		ExportOperation eo = new ExportOperation(exporter, buf, start, end, ExportProgressCallback, OnExportFinished);
		
		CloseButton.Sensitive = false;
		ExportButton.Sensitive = false;
		
		// start export thread
		Thread exportThread=new Thread(eo.OperationThread);
		exportThread.IsBackground=true;
		exportThread.Start();
				
		return new ThreadedAsyncResult(eo, exportFinishedEvent, false);
	}
	
	private void OnExportFinished(IAsyncResult ar)
	{
	lock (LockObj) {
		ExportOperation eo=(ExportOperation)ar.AsyncState;
		
		if (eo.Result == ExportOperation.OperationResult.Finished) {
			Services.Info.DisplayMessage(string.Format("Exported data to '{0}'",  (eo.Exporter.Builder.OutputStream as FileStream).Name));
			this.Hide();
		}
		else if (eo.Result == ExportOperation.OperationResult.CaughtException) {
			ErrorAlert ea;
			if (eo.ThreadException.GetType() == typeof(FormatException))
				ea = new ErrorAlert("Export Pattern Error", eo.ThreadException.Message, mainWindow);
			else
				ea = new ErrorAlert("Exporting Error", eo.ThreadException.Message, mainWindow);
				
			ea.Run();
			ea.Destroy();
		}
		
		if (eo.Exporter.Builder.OutputStream != null)
			eo.Exporter.Builder.OutputStream.Close();
			
		CloseButton.Sensitive = true;
		ExportButton.Sensitive = true;
	}
	}
	
	private bool ExportProgressCallback(object o, ProgressAction action)
	{
		if (action == ProgressAction.Hide) {
			ProgressHBox.Visible = false;
			return false; 
		}
		else if (action == ProgressAction.Show) {
			ProgressHBox.Visible = true;
			return false;
		}
		
		
		if ((double)o > 1.0)
			o = 1.0;
		
		ExportProgressBar.Fraction=(double)o;
		
		return cancelClicked;
	}
	
	void OnDialogResponse(object o, Gtk.ResponseArgs args)
	{
		lock (LockObj) {
		if (args.ResponseId == ResponseType.Ok && dataBook!=null && dataBook.NPages > 0) {
			DataView dv=((DataViewDisplay)dataBook.CurrentPageWidget).View;
			
			IExportBuilder builder = null;
			TreeIter iter;
			ExportAsCombo.GetActiveIter(out iter);
			ExportPlugin plugin = (ExportPlugin) ExportAsCombo.Model.GetValue(iter, 1);
			
			
			Util.Range range;
			
			try {
				range = GetCurrentRange(dv);
			}
			catch(FormatException ex) {
				ErrorAlert ea = new ErrorAlert("Error in custom range", ex.Message, mainWindow);
				ea.Run();
				ea.Destroy();
				return;
			}
			
			Util.Range bufferRange;
			if (dv.Buffer.Size == 0)
				bufferRange = new Util.Range();
			else
				bufferRange = new Util.Range(0, dv.Buffer.Size - 1);
			
			if (!bufferRange.Contains(range.Start) || !bufferRange.Contains(range.End)) {
				ErrorAlert ea = new ErrorAlert("Error in range", "Range is out of file's limits", mainWindow);
				ea.Run();
				ea.Destroy();
				return;
			}
			
			Stream stream = null;
			try {
				stream = new FileStream(ExportFileEntry.Text, FileMode.Create, FileAccess.Write);
				builder = plugin.CreateBuilder(stream);
						
				InterpretedPatternExporter exporter = new InterpretedPatternExporter(builder);
				exporter.Pattern = ExportPatternEntry.Text;
				
				cancelClicked = false;
				BeginExport(exporter, dv.Buffer, range.Start, range.End);
				
			}
			catch(Exception ex) {
				if (stream != null)
					stream.Close();
					
				ErrorAlert ea = new ErrorAlert("Error saving to file", ex.Message, mainWindow);
				ea.Run();
				ea.Destroy();
				return;
			}
		}
		else if (args.ResponseId == ResponseType.Close)
			this.Hide();
		}
	}
	
}


} // end namespace