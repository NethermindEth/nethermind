using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Terminal.Gui;

namespace Nethermind.RocksDbExtractor

{
    class Program
    {
        static void Main(string[] args)
        {
            Application.Init ();
            var top = Application.Top;

            // Creates a menubar, the item "New" has a help menu.
            var menu = new MenuBar (new MenuBarItem [] {
                new MenuBarItem ("_File", new MenuItem [] {
                    new MenuItem ("_Quit", "", () => { top.Running = false; })
                })
            });
            top.Add (menu);


            // Creates the top-level window to show
            var win = new Window (new Rect (0, 1, top.Frame.Width, top.Frame.Height-1), "Movie Db");
            top.Add (win);

            // Add some controls
            var txtSearchLbl = new Label(3, 1, "Movie Name: ");
            var txtSearch = new TextField(15, 1, 30, "");
            var forKidsOnly = new CheckBox(3, 3, "For Kids?");
            var minimumRatingLbl = new Label(25, 3, "Minimum Rating: ");
            var minimumRatingTxt = new TextField(41, 3, 10, "");
            var searchBtn = new Button(3, 5, "Filter");
            var mylist = new List<string>();
            mylist.Add("1");
            mylist.Add("2");
            mylist.Add("3");
            var allMoviesListView = new ListView(new Rect(4, 8, top.Frame.Width, 200), mylist);
            searchBtn.Clicked = () =>
            {

                double rating = 0;
                var isDouble = double.TryParse(minimumRatingTxt.Text.ToString(), out rating);
                if(!string.IsNullOrEmpty(minimumRatingTxt.Text.ToString()) && !isDouble)
                {
                    MessageBox.ErrorQuery(30, 6, "Error", "Rating must be number");
                    minimumRatingTxt.Text = string.Empty;
                    return;
                }

                win.Remove(allMoviesListView);
                if (string.IsNullOrEmpty(txtSearch.Text.ToString()) || string.IsNullOrEmpty(minimumRatingTxt.Text.ToString()))
                {
                    allMoviesListView = new ListView(new Rect(4, 8, top.Frame.Width, 200), mylist);
                    win.Add(allMoviesListView);
                }
                else
                {
                    win.Remove(allMoviesListView);
                    win.Add(new ListView(new Rect(4, 8, top.Frame.Width, 200), mylist));
                }

            };
            win.Add (
                txtSearchLbl,
                txtSearch,
                forKidsOnly,
                minimumRatingLbl,
                minimumRatingTxt,
                searchBtn,
                new Label (3, 7, "-------------Search Result--------------")
            );

            Application.Run ();
        }
    }
}