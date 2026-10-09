using Tasklabe.Core.Domain;
using Tasklabe.Core.Editing;

namespace Tasklabe.Core.Tests;

public class PeopleTests
{
    [Fact]
    public void Guests_are_shown_by_name_and_users_by_login()
    {
        Assert.Equal("山田 太郎", People.Display(People.Guest("山田 太郎")));
        Assert.Equal("@alice", People.Display("alice"));
        Assert.Equal("山田 太郎", People.Name(People.Guest("山田 太郎")));
    }

    [Fact]
    public void Guest_names_are_saved_sorted_without_users()
    {
        Assert.Equal("Sato, 山田", People.GuestNames(["alice", People.Guest("山田"), People.Guest("Sato"), People.Guest("sato")]));
        Assert.Null(People.GuestNames(["alice"]));
        Assert.Equal(["alice"], People.Logins(["alice", People.Guest("山田")]));
    }

    [Fact]
    public void Saved_text_is_read_back_as_guests()
    {
        Assert.Equal([People.Guest("Sato"), People.Guest("山田")], People.ParseGuestNames("Sato, 山田"));
        Assert.Equal([People.Guest("山田 太郎"), People.Guest("佐藤")], People.ParseGuestNames("山田　太郎\n佐藤,,"));
        Assert.Empty(People.ParseGuestNames(null));
    }

    [Fact]
    public void Names_that_cannot_be_saved_are_rejected()
    {
        Assert.NotNull(People.GuestNameProblem(""));
        Assert.NotNull(People.GuestNameProblem("山田,佐藤"));
        Assert.NotNull(People.GuestNameProblem("@alice"));
        Assert.NotNull(People.GuestNameProblem(new string('あ', People.MaxGuestNameLength + 1)));
        Assert.Null(People.GuestNameProblem("山田 太郎"));
        Assert.Equal("山田 太郎", People.NormalizeGuestName("  山田　　太郎 "));
    }

    [Fact]
    public void Guests_and_users_share_one_assignee_value()
    {
        var value = TaskValues.Logins(["bob", People.Guest("山田"), "alice"]);

        Assert.Equal(["alice", "bob", People.Guest("山田")], TaskValues.ParseLogins(value));
    }
}
