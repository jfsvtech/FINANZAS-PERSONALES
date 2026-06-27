using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FinanzasPersonales.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace FinanzasPersonales.Web.Controllers;

public class DocumentacionController : BaseController
{
    private readonly IWebHostEnvironment _env;

    private static readonly Dictionary<string, DocumentoArchivo> Archivos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["funcional"] = new("01_documentacion_funcional.md", "01_documentacion_funcional.pdf", "Documentacion funcional.pdf", "Documentacion funcional", "Guia de uso del sistema, modulos, permisos y operacion diaria.", false),
        ["tecnica"] = new("02_documentacion_tecnica.md", "02_documentacion_tecnica.pdf", "Documentacion tecnica.pdf", "Documentacion tecnica", "Arquitectura, tecnologias, seguridad, base de datos e integraciones.", true),
        ["despliegue"] = new("03_documentacion_despliegue.md", "03_documentacion_despliegue.pdf", "Documentacion de despliegue.pdf", "Documentacion de despliegue", "Publicacion, variables, PostgreSQL, SMTP, WhatsApp y consideraciones sobre Vercel.", true)
    };

    public DocumentacionController(IWebHostEnvironment env)
    {
        _env = env;
    }

    public IActionResult Index()
    {
        var vm = new DocumentacionIndexVm
        {
            EsAdmin = EsAdmin,
            Documentos = CrearDocumentos(),
            SeccionesFuncionales = CrearSeccionesFuncionales()
        };

        return View(vm);
    }

    public IActionResult Pdf(string id)
    {
        if (!Archivos.TryGetValue(id ?? "", out var doc))
            return NotFound();
        if (doc.SoloAdmin && !EsAdmin)
            return Forbid();

        var ruta = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "output", "pdf", doc.Archivo));
        var raizPdf = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "output", "pdf"));
        if (!ruta.StartsWith(raizPdf, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(ruta))
        {
            TempData["Error"] = "El PDF no existe todavia. Regenera la documentacion desde el proyecto.";
            return RedirectToAction("Index");
        }

        return PhysicalFile(ruta, "application/pdf", doc.NombreDescarga, enableRangeProcessing: true);
    }

    public IActionResult Ver(string id)
    {
        id = id ?? "";
        if (!Archivos.TryGetValue(id, out var doc))
            return NotFound();
        if (doc.SoloAdmin && !EsAdmin)
            return Forbid();

        var ruta = ObtenerRutaMarkdown(doc.Markdown);
        if (!System.IO.File.Exists(ruta))
        {
            TempData["Error"] = "El documento fuente no existe.";
            return RedirectToAction("Index");
        }

        var markdown = System.IO.File.ReadAllText(ruta, Encoding.UTF8);
        if (id.Equals("funcional", StringComparison.OrdinalIgnoreCase) && !EsAdmin)
            markdown = FiltrarFuncionalPorPermisos(markdown);

        return View(new DocumentoDetalleVm
        {
            Codigo = id,
            Titulo = doc.Titulo,
            Descripcion = doc.Descripcion,
            Html = MarkdownBasicoAHtml(markdown),
            PuedeDescargarPdf = true
        });
    }

    private List<DocumentoAyudaVm> CrearDocumentos()
    {
        var documentos = new List<DocumentoAyudaVm>
        {
            new()
            {
                Codigo = "funcional",
                Titulo = "Documentacion funcional",
                Descripcion = Archivos["funcional"].Descripcion,
                Icono = "bi-book",
                Nivel = "Usuarios"
            }
        };

        if (EsAdmin)
        {
            documentos.Add(new()
            {
                Codigo = "tecnica",
                Titulo = "Documentacion tecnica",
                Descripcion = Archivos["tecnica"].Descripcion,
                Icono = "bi-code-slash",
                Nivel = "Administracion"
            });
            documentos.Add(new()
            {
                Codigo = "despliegue",
                Titulo = "Documentacion de despliegue",
                Descripcion = Archivos["despliegue"].Descripcion,
                Icono = "bi-cloud-upload",
                Nivel = "Administracion"
            });
        }

        return documentos;
    }

    private List<DocumentoSeccionVm> CrearSeccionesFuncionales()
    {
        var secciones = new List<DocumentoSeccionVm>
        {
            new()
            {
                Titulo = "Acceso y seguridad",
                Descripcion = "Inicio de sesion, verificacion de correo, recuperacion y cambio de contrasena.",
                Icono = "bi-shield-lock",
                Puntos = new()
                {
                    "Acceso con correo electronico verificado.",
                    "Recuperacion de contrasena por enlace temporal.",
                    "Bloqueo por intentos fallidos y contrasenas fuertes."
                }
            }
        };

        if (TienePermiso("PermisoGastos"))
        {
            secciones.Add(new()
            {
                Titulo = "Gestion de Finanzas",
                Descripcion = "Movimientos, recurrentes, presupuestos, metas, cuentas, categorias y analisis financiero.",
                Icono = "bi-wallet2",
                Puntos = new()
                {
                    "Registro de ingresos, gastos, transferencias y pagos de tarjeta.",
                    "Movimientos recurrentes de ingreso o gasto.",
                    "Categorias y cuentas personalizadas por usuario.",
                    "Presupuestos, metas e informes graficos."
                }
            });
        }

        if (TienePermiso("PermisoPrestamos"))
        {
            secciones.Add(new()
            {
                Titulo = "Gestion de Prestamos",
                Descripcion = "Personas, prestamos activos, intereses, abonos, cobros y analisis de cartera.",
                Icono = "bi-cash-stack",
                Puntos = new()
                {
                    "Registro de prestamos por persona.",
                    "Calculo de capital e interes pendiente.",
                    "Resumen completo por persona.",
                    "Analisis y proximas fechas de cobro."
                }
            });
        }

        if (TienePermiso("PermisoInversiones"))
        {
            secciones.Add(new()
            {
                Titulo = "Gestion de Inversiones",
                Descripcion = "Portafolio, tipos de inversion personalizados, retornos, movimientos y valoraciones.",
                Icono = "bi-briefcase",
                Puntos = new()
                {
                    "Tipos de inversion configurables por usuario.",
                    "Registro de capital, tasa, permanencia y retorno.",
                    "Aportes, retiros, rendimientos y valoraciones.",
                    "Analisis de distribucion y retorno esperado."
                }
            });
        }

        if (TienePermiso("PermisoCalendario"))
        {
            secciones.Add(new()
            {
                Titulo = "Calendario financiero",
                Descripcion = "Vista mensual con tarjetas, recurrentes, prestamos, metas e inversiones.",
                Icono = "bi-calendar3",
                Puntos = new()
                {
                    "Eventos financieros en formato calendario.",
                    "Lista de proximos vencimientos.",
                    "Acciones directas para registrar pagos, cobros o rendimientos."
                }
            });
        }

        if (TienePermiso("PermisoAsistente"))
        {
            secciones.Add(new()
            {
                Titulo = "Asistente financiero",
                Descripcion = "Recordatorios, informes, recomendaciones y registro rapido.",
                Icono = "bi-stars",
                Puntos = new()
                {
                    "Recordatorios por correo o WhatsApp cuando este configurado.",
                    "Informe mensual.",
                    "Registro rapido por lenguaje natural."
                }
            });
        }

        if (TienePermiso("PermisoDirectivo"))
        {
            secciones.Add(new()
            {
                Titulo = "Analisis directivo",
                Descripcion = "Indicadores ejecutivos y vision consolidada para usuarios con permiso.",
                Icono = "bi-gem",
                Puntos = new()
                {
                    "Resumen de alto nivel.",
                    "Comparativos y alertas principales.",
                    "Acceso reservado por permiso."
                }
            });
        }

        if (EsAdmin)
        {
            secciones.Add(new()
            {
                Titulo = "Administracion",
                Descripcion = "Usuarios, suscripciones, permisos, integraciones y documentacion tecnica.",
                Icono = "bi-shield-check",
                Puntos = new()
                {
                    "Crear usuarios y definir permisos.",
                    "Controlar suscripciones, pagos y morosidad.",
                    "Configurar SMTP y WhatsApp desde la interfaz.",
                    "Consultar documentacion tecnica y de despliegue."
                }
            });
        }

        return secciones;
    }

    private string ObtenerRutaMarkdown(string archivo)
    {
        var ruta = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "docs", archivo));
        var raiz = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "docs"));
        return ruta.StartsWith(raiz, StringComparison.OrdinalIgnoreCase) ? ruta : "";
    }

    private string FiltrarFuncionalPorPermisos(string markdown)
    {
        var excluir = new List<string>();
        if (!TienePermiso("PermisoGastos")) excluir.Add("## 5. Gestion de Finanzas");
        if (!TienePermiso("PermisoPrestamos")) excluir.Add("## 6. Gestion de Prestamos");
        if (!TienePermiso("PermisoInversiones")) excluir.Add("## 7. Gestion de Inversiones");
        if (!TienePermiso("PermisoCalendario")) excluir.Add("## 8. Calendario financiero");
        if (!TienePermiso("PermisoAsistente")) excluir.Add("## 9. Asistente financiero");
        if (!TienePermiso("PermisoDirectivo")) excluir.Add("## 10. Analisis directivo");
        if (!EsAdmin)
        {
            excluir.Add("## 11. Administracion de usuarios");
            excluir.Add("## 12. Integraciones");
        }

        foreach (var encabezado in excluir)
            markdown = QuitarSeccion(markdown, encabezado);
        return markdown;
    }

    private static string QuitarSeccion(string markdown, string encabezado)
    {
        var inicio = markdown.IndexOf(encabezado, StringComparison.OrdinalIgnoreCase);
        if (inicio < 0) return markdown;
        var siguiente = markdown.IndexOf("\n## ", inicio + encabezado.Length, StringComparison.OrdinalIgnoreCase);
        return siguiente < 0
            ? markdown[..inicio].TrimEnd() + Environment.NewLine
            : markdown[..inicio] + markdown[siguiente..];
    }

    private static string MarkdownBasicoAHtml(string markdown)
    {
        var html = new StringBuilder();
        var enLista = false;
        var enCodigo = false;
        var codigo = new StringBuilder();

        void CerrarLista()
        {
            if (!enLista) return;
            html.AppendLine("</ul>");
            enLista = false;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (!enCodigo)
                {
                    CerrarLista();
                    enCodigo = true;
                    codigo.Clear();
                }
                else
                {
                    html.Append("<pre><code>");
                    html.Append(WebUtility.HtmlEncode(codigo.ToString().TrimEnd()));
                    html.AppendLine("</code></pre>");
                    enCodigo = false;
                }
                continue;
            }

            if (enCodigo)
            {
                codigo.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CerrarLista();
                continue;
            }

            if (line.StartsWith("# "))
            {
                CerrarLista();
                html.AppendLine($"<h1>{Inline(line[2..])}</h1>");
            }
            else if (line.StartsWith("## "))
            {
                CerrarLista();
                html.AppendLine($"<h2>{Inline(line[3..])}</h2>");
            }
            else if (line.StartsWith("### "))
            {
                CerrarLista();
                html.AppendLine($"<h3>{Inline(line[4..])}</h3>");
            }
            else if (line.StartsWith("- "))
            {
                if (!enLista)
                {
                    html.AppendLine("<ul>");
                    enLista = true;
                }
                html.AppendLine($"<li>{Inline(line[2..])}</li>");
            }
            else
            {
                CerrarLista();
                html.AppendLine($"<p>{Inline(line)}</p>");
            }
        }

        CerrarLista();
        return html.ToString();
    }

    private static string Inline(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        encoded = Regex.Replace(encoded, @"`(.+?)`", "<code>$1</code>");
        return encoded;
    }

    private sealed record DocumentoArchivo(string Markdown, string Archivo, string NombreDescarga, string Titulo, string Descripcion, bool SoloAdmin);
}
