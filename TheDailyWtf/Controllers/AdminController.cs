using System;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;
using System.Web.Security;
using Inedo.Diagnostics;
using Newtonsoft.Json;
using TheDailyWtf.Data;
using TheDailyWtf.Forum;
using TheDailyWtf.Models;
using TheDailyWtf.Security;
using TheDailyWtf.ViewModels;

namespace TheDailyWtf.Controllers
{
    [Authorize]
    public class AdminController : WtfControllerBase
    {
        //
        // GET: /Admin/

        public ActionResult Index()
        {
            if (!this.User.IsAdmin)
                return Redirect("/admin/my-articles");

            return Redirect("/admin/articles");
        }

        [AllowAnonymous]
        public ActionResult Login()
        {
            if (this.User != null)
                return Redirect("/admin");

            return View();
        }

        [AllowAnonymous]
        public ActionResult Logout()
        {
            FormsAuthentication.SignOut();

            return RedirectToAction("login");
        }

        public ActionResult ReenableSideBar()
        {
            ForumHelper.UnpauseConnections();

            return Redirect("/admin");
        }

        public ActionResult MyArticles()
        {
            return View(new MyArticlesViewModel(this.User.Identity.Name));
        }

        [RequiresAdmin]
        public ActionResult ArticleList()
        {
            return View(new AdminViewModel());
        }

        [RequiresAdmin]
        public ActionResult SeriesList()
        {
            return View(new AdminViewModel());
        }

        [RequiresAdmin]
        public ActionResult LoginList()
        {
            return View(new AdminViewModel());
        }

        [RequiresAdmin]
        public ActionResult FooterAdList()
        {
            return View(new AdminViewModel());
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public ActionResult Login(string username, string password)
        {
            bool validLogin = StoredProcs.Authors_ValidateLogin(username, password).Execute().Value;

            if (validLogin)
            {
                var author = AuthorModel.GetAuthorBySlug(username);
                var principal = new AuthorPrincipal(author);

                var userData = JsonConvert.SerializeObject(principal.ToSerializableModel());
                var expiresDate = DateTime.Now.AddMinutes(30);
                var authTicket = new FormsAuthenticationTicket(1, author.Slug, DateTime.Now, expiresDate, false, userData);

                string encTicket = FormsAuthentication.Encrypt(authTicket);
                var cookie = new HttpCookie(FormsAuthentication.FormsCookieName, encTicket)
                {
                    HttpOnly = true,
                    Expires = expiresDate,
                    Path = FormsAuthentication.FormsCookiePath
                };
                this.Response.Cookies.Add(cookie);
                var cookieIsAdmin = new HttpCookie("IS_ADMIN", "1")
                {
                    HttpOnly = false,
                    Expires = expiresDate,
                    Path = FormsAuthentication.FormsCookiePath
                };
                this.Response.Cookies.Add(cookieIsAdmin);

                return new RedirectResult(FormsAuthentication.GetRedirectUrl(author.Slug, false));
            }

            return View();
        }

        public ActionResult EditArticle(int? id)
        {
            var model = new EditArticleViewModel(id) { User = this.User };
            if (model.UserCanEdit)
                return View(model);
            else
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [ValidateInput(false)]
        public ActionResult EditArticle(EditArticleViewModel post)
        {
            if (string.IsNullOrEmpty(post.Article.Series.Slug))
                this.ModelState.AddModelError(string.Empty, "A series is required");
            if (string.IsNullOrEmpty(post.Article.Author.Slug))
                this.ModelState.AddModelError(string.Empty, "An author is required");
            if (!string.IsNullOrEmpty(post.Article.Author.Slug) && !this.User.IsAdmin && post.Article.Author.Slug != this.User.Identity.Name)
                this.ModelState.AddModelError(string.Empty, "Only administrators can change authors.");
            if (!this.ModelState.IsValid)
                return View(post);

            try
            {
                Logger.Information("Creating or updating article \"{0}\".", post.Article.Title);
                int? articleId = StoredProcs.Articles_CreateOrUpdateArticle(
                    post.Article.Id,
                    post.Article.Slug ?? this.User.Identity.Name,
                    post.PublishedDate,
                    post.Article.Status,
                    post.Article.Author.Slug,
                    post.Article.Title,
                    post.Article.Series.Slug,
                    post.Article.BodyHtml,
                    post.Article.DiscourseTopicId
                  ).Execute();

                post.Article.Id = post.Article.Id ?? articleId;

                return RedirectToRoute(this.User.IsAdmin ? "ArticleListAdmin" : "MyArticlesAdmin");
            }
            catch (Exception ex)
            {
                post.ErrorMessage = ex.ToString();
                return View(post);
            }
        }

        public ActionResult ArticleComments(int id, int page)
        {
            if (this.User == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            var article = ArticleModel.GetArticleById(id);
            if (article == null)
                return new HttpStatusCodeResult(HttpStatusCode.NotFound);
            if (this.User.IsAdmin)
                return View(new ArticleCommentsViewModel(article, page, true));
            if (this.User.Identity.Name != article.Author.Slug)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            return View(new ArticleCommentsViewModel(article, page, false));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequiresAdmin]
        public ActionResult DeleteComments(DeleteCommentsModel post)
        {
            var commentIdsCsv = string.Join(",", post.Delete);
            Logger.Information("Deleting comments with IDs \"{0}\".", commentIdsCsv);

            StoredProcs.Comments_DeleteComments(commentIdsCsv).Execute();

            return Redirect(Request.UrlReferrer.ToString());
        }

        [RequiresAdmin]
        public ActionResult CommentsByIP(string ip, int page)
        {
            if (this.User == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            return View(new UserCommentsViewModel("by-ip/" + Url.Encode(ip), CommentModel.GetCommentsByIP(ip), page));
        }

        [RequiresAdmin]
        public ActionResult CommentsByToken(string token, int page)
        {
            if (this.User == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            return View(new UserCommentsViewModel("by-token/" + Url.Encode(token), CommentModel.GetCommentsByToken(token), page));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequiresAdmin]
        [ValidateInput(false)]
        public ActionResult EditComment(int article, int comment, string body, string name)
        {
            var articleModel = ArticleModel.GetArticleById(article);
            if (articleModel == null)
                return new HttpStatusCodeResult(HttpStatusCode.NotFound);
            var commentModel = CommentModel.FromArticle(articleModel).First(c => c.Id == comment);
            if (commentModel == null)
                return new HttpStatusCodeResult(HttpStatusCode.NotFound);
            if (body == null)
            {
                body = commentModel.BodyRaw;
                name = commentModel.Username;
                return View(new EditCommentViewModel { Article = articleModel, Comment = commentModel, Post = new CommentFormModel { Body = body, Name = name } });
            }
            StoredProcs.Comments_CreateOrUpdateComment(comment, article, body, name, commentModel.PublishedDate, commentModel.UserIP, commentModel.UserToken, commentModel.ParentCommentId).ExecuteNonQuery();
            return RedirectToRoute("ArticleCommentsAdmin", new { id = articleModel.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult FeatureComment(FeatureCommentViewModel post)
        {
            if (this.User == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            if (post.Article == null || post.Comment == null)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            var article = ArticleModel.GetArticleById((int)post.Article);
            if (article == null)
                return new HttpStatusCodeResult(HttpStatusCode.NotFound);
            if (!this.User.IsAdmin && this.User.Identity.Name != article.Author.Slug)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            if (!StoredProcs.Articles_FeatureComment(article.Id, post.Comment).Execute().Value)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            return RedirectToRoute("ArticleCommentsAdmin", new { id = article.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult UnfeatureComment(FeatureCommentViewModel post)
        {
            if (this.User == null)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            if (post.Article == null || post.Comment == null)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            var article = ArticleModel.GetArticleById((int)post.Article);
            if (article == null)
                return new HttpStatusCodeResult(HttpStatusCode.NotFound);
            if (!this.User.IsAdmin && this.User.Identity.Name != article.Author.Slug)
                return new HttpStatusCodeResult(HttpStatusCode.Forbidden);
            if (!StoredProcs.Articles_UnfeatureComment(article.Id, post.Comment).Execute().Value)
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            return RedirectToRoute("ArticleCommentsAdmin", new { id = article.Id });
        }

        [RequiresAdmin]
        public ActionResult EditSeries(string slug)
        {
            return View(new EditSeriesViewModel(slug));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequiresAdmin]
        public ActionResult EditSeries(EditSeriesViewModel post)
        {
            StoredProcs.Series_CreateOrUpdateSeries(
                post.Series.Slug,
                post.Series.Title,
                post.Series.Description
              ).Execute();

            return RedirectToRoute("SeriesListAdmin");
        }

        [RequiresAdmin]
        public ActionResult EditAd(int? id)
        {
            return View(new EditAdViewModel(id));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequiresAdmin]
        public ActionResult EditAd(EditAdViewModel post)
        {
            StoredProcs.Ads_CreateOrUpdateAd(post.Ad.BodyHtml, post.Ad.Id).Execute();

            return RedirectToRoute("FooterAdListAdmin");
        }

        [RequiresAdmin]
        [ValidateAntiForgeryToken]
        [HttpPost]
        public ActionResult DeleteAd(int id)
        {
            StoredProcs.Ads_DeleteAd(id).Execute();

            return RedirectToRoute("FooterAdListAdmin");
        }

        public ActionResult EditAuthor(string slug)
        {
            return View(new EditAuthorViewModel(slug));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequiresAdmin]
        public ActionResult EditAuthor(EditAuthorViewModel post)
        {
            StoredProcs.Authors_CreateOrUpdateAuthor(
                post.Author.Slug,
                post.Author.Name,
                post.Author.IsAdmin,
                post.Author.DescriptionHtml,
                post.Author.ShortDescription,
                Inedo.InedoLib.Util.NullIf(post.Author.ImageUrl, string.Empty),
                post.Author.IsActive
              ).Execute();

            if (!string.IsNullOrEmpty(post.Password))
            {
                StoredProcs.Authors_SetPassword(post.Author.Slug, post.Password).Execute();
            }

            return RedirectToRoute("LoginListAdmin");
        }

        public ActionResult ViewAds(DateTime? start, DateTime? end)
        {
            if (!this.User.IsAdmin)
                return RedirectToRoute("MyArticlesAdmin");

            return View(new ViewAdsViewModel(start, end));
        }
    }
}
