using System.Windows.Media.Imaging;

namespace EmpireBay
{
    public sealed class Scene
    {
        public string Title { get; private set; }
        public string Caption { get; private set; }
        public BitmapSource Picture { get; private set; }

        Scene(string file, string title, string caption)
        {
            Title = title;
            Caption = caption;
            Picture = ReadImage(file + ".jpg");
        }

        public static BitmapSource ReadImage(string resource)
        {
            using (var file = Settings.Resource(resource))
            {
                var image = BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze();
                return image;
            }
        }

        public static Scene[] Album()
        {
            return new[] {
                new Scene("hot-rod", "Hot rod", "El hot rod del distrito industrial"),
                new Scene("ciudad", "La ciudad", "El perfil de Empire Bay"),
                new Scene("comisaria", "Comisaría", "Departamento de Policía de Empire Bay"),
                new Scene("patrulla", "Patrulla", "Patrulla nocturna"),
                new Scene("restaurante", "Restaurante", "Una mesa para dos"),
                new Scene("armeria", "Armería", "El mostrador de la armería"),
                new Scene("reunion", "La reunión", "Una reunión en el restaurante")
            };
        }
    }
}
